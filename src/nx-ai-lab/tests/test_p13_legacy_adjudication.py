"""Independent synthetic cases; optional pinned archive checks use only user-supplied ZIPs."""
from contextlib import closing
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from hashlib import sha256
import io
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest
from zipfile import ZipFile

from ai_studio.task_store import TaskStore, canonical
from ai_studio.legacy_adjudication import (
    A, B, FAILURE, FIXED, ISSUED_A, Material, Issuance, PERSISTENT, RETAINED,
    SUPPRESSED, Verdict, verify_case,
)
from ai_studio.legacy_adjudication_index import isolated_index


def raw(value):
    return canonical(value).encode()


def sha(value):
    return sha256(value).hexdigest()


def stamp(i):
    return (datetime(2000, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=i)).isoformat()


def bind(doc, material):
    """Test signer operates separately from candidate verification, only for synthetic tasks."""
    if not material.task_id.startswith("synthetic:"):
        raise ValueError("Real records cannot be test-signed")
    doc = dict(doc)
    doc.pop("decision_record_canonical_sha256", None)
    doc["decision_record_canonical_sha256"] = sha(raw(doc))
    material = replace(material, decision=raw(doc))
    credential = Issuance(material.task_id, material.operation_id, sha(material.decision),
                          sha(material.task_spec), "synthetic:independent-reviewer/fixture-42")
    return material, credential


def fixture(changed_constraint=None, extra_unknown=False):
    task = "synthetic:display-fixture"
    spec = raw({"task_id": task})
    part = "SYNTHETIC:/assembly.prt"
    receipts = {}
    with tempfile.TemporaryDirectory(prefix="p13-source-fixture-") as temp:
        store = TaskStore(temp)
        with closing(store.db) as db:
            db.execute("INSERT INTO task_meta VALUES (?,?)", ("synthetic_task_id", task))

            def op(key, command, response, tick):
                payload = {"command": command}
                digest = sha(raw(payload))
                state = "succeeded" if response["ok"] else "failed"
                db.execute("INSERT INTO operations VALUES (?,?,?,?,?,?,?,?,?)",
                           (key, canonical(payload), digest, "nx_command", state, "fixture",
                            stamp(tick), stamp(tick + 1), canonical(response)))
                receipts[key] = raw({"operation_id": key, "payload_digest": digest, "response": response,
                                     "provenance": "synthetic application receipt"})
                return payload, digest, response

            def event(n, tick, command, response):
                db.execute("INSERT INTO events VALUES (?,?,?,?)", (n, stamp(tick), "nx_observation",
                           canonical({"command": command, "response": response})))

            shape = {"ok":True,"units":"Millimeters","part":"SYNTHETIC:/base.prt",
                     "bodies":[{"solid":True,"consistency_fault_count":0,"faults":[],"volume_mm3":48000}]}
            cut = dict(shape, bodies=[dict(shape["bodies"][0],volume_mm3=47033.89742716806)])
            event(1, 1, "nx_inspect", shape)
            af = op(A, "nx_hole EXTRUDE(1) 0 0 12.4 8 0", {"ok":False,"error":"unknown body_id: EXTRUDE(1)"}, 2)
            event(2, 4, "nx_inspect", shape)
            ar = op("base.bore.repair1", "nx_hole BODY_1 0 0 12.4 8 0", {"ok":True,"result":"hole"}, 5)
            event(3, 7, "nx_inspect", cut)
            op("base.verify_open", "nx_open_part SYNTHETIC:/base.prt", {"ok":True,"part":"SYNTHETIC:/base.prt"}, 8)
            event(4, 10, "nx_inspect", cut)

            bf = op(B, "nx_constraint distance conflicting_seat PEDESTAL 0:1 BASE 0:5 2 opposite",
                    {"ok": False, "name": "conflicting_seat", "type": "Distance",
                     "constraint_retained_for_diagnosis": True, "solver_status": "OverConstrained"}, 20)
            event(5, 22, "nx_assembly_state", {"ok":True, "part":part, "constraints":[
                {"name":"conflicting_seat", "expression":"2", "status":"OverConstrained","suppressed":False}]})
            br = op("repair.seat_value","nx_constraint_value conflicting_seat 0",
                    {"ok":True,"name":"conflicting_seat","solver_status":"Solved","suppressed":False}, 23)
            for i, name in enumerate(SUPPRESSED):
                op("assembly.suppress."+name, "nx_constraint_suppress "+name+" 1",
                   {"ok":True,"name":name,"suppressed":True,"solver_status":"Suppressed"}, 25+i*2)
            op("assembly.fix.pedestal", "nx_constraint fix fix_pedestal PEDESTAL component -",
               {"ok":True,"name":"fix_pedestal","solver_status":"Solved"}, 31)
            op("assembly.save","nx_save_part",{"ok":True,"saved":part}, 33)
            op("assembly.close","nx_close_part 0",{"ok":True,"result":"closed part"}, 35)
            op("assembly.reopen","nx_open_part "+part,{"ok":True,"part":part}, 37)
            constraints = []
            for name in SUPPRESSED + FIXED:
                suppressed = name in SUPPRESSED
                typ = "Distance" if name in SUPPRESSED[:2] else ("Touch" if suppressed else "Fix")
                status = "Suppressed" if suppressed else "Solved"
                constraints.append(dict(name=name,type=typ,expression="0" if typ=="Distance" else None,
                                        suppressed=not suppressed if name==changed_constraint and suppressed else suppressed,
                                        status="Broken" if name==changed_constraint and not suppressed else status))
            event(6, 39, "nx_assembly_state", {"ok":True,"part":part,"unsatisfied_constraint_count":0,
                                                "constraints":constraints})
            if extra_unknown:
                db.execute("INSERT INTO operations VALUES (?,?,?,?,?,?,?,?,?)",
                           ("independent.unknown", "{}", sha(b"{}"), "nx_command", "unknown", "fixture",
                            stamp(40), stamp(41), None))
            db.commit()
        snapshot = (Path(temp)/"task.sqlite").read_bytes()
    acceptance = raw(dict(task_id=task,operation_id=B,part=part,static_display_accepted=True,
                          suppressed=list(SUPPRESSED),solved=list(FIXED)))
    cases = {}
    for ident, orig, repair_id, repair in [(A,af,"base.bore.repair1",ar),(B,bf,"repair.seat_value",br)]:
        scope = dict(task_id=task,operation_id=ident,only_this_operation=True,nx_smoke="NOT_RUN",
                     production_shared_database_write=False,domain_wide_stop_c_release=False,
                     historical_command_replay=False)
        decision = dict(application_outcome=FAILURE if ident==A else PERSISTENT,
                        historical_operation_state_preserved="failed",transport_outcome="UNKNOWN_NOT_PROVEN",
                        replay_allowed=False,safe_to_clear_production_stop_c=False,production_mutation_authorized=False)
        basis = {"repair_operation_id":repair_id}
        for k, fields, receipt_id in [("original",orig,ident),("repair",repair,repair_id)]:
            basis.update({k+"_payload":fields[0],k+"_payload_digest":fields[1],
                          k+"_application_response":fields[2],k+"_application_receipt_sha256":sha(receipts[receipt_id])})
        if ident == A:
            decision["observed_geometry_effect"]="NO_CHANGE_OBSERVED_BETWEEN_PRE_OPERATION_AND_PRE_REPAIR_INSPECTIONS"
            for key, observation in [("before_original",shape),("before_repair",shape),("after_repair",cut),("reopened",cut)]:
                basis[key+"_inspection_response_sha256"]=sha(raw(observation))
        else:
            decision["side_effect_classification"]=RETAINED
            basis.update(assembly_path=part,fault_event=5,reopened_event=6,static_acceptance_sha256=sha(acceptance))
        doc=dict(record_type="P13_LEGACY_OPERATION_ADJUDICATION", record_status="ISSUED_FOR_CASE_SPECIFIC_HISTORICAL_CLASSIFICATION",
                 decision_id="synthetic-decision-"+ident,scope=scope,decision=decision,basis=basis,
                 evidence_binding=dict(task_database_sha256=sha(snapshot),task_database_bytes=len(snapshot)))
        material=Material(task,ident,b"",spec,snapshot,receipts[ident],receipts[repair_id],acceptance if ident==B else b"")
        cases[ident]=bind(doc,material)
    return cases


def amended(pair, edit):
    material, _ = pair
    doc=json.loads(material.decision)
    edit(doc)
    return bind(doc,material)


def verdict(pair):
    return verify_case(pair[0],fixture_issuance=pair[1])


def inspect_db(path):
    with closing(sqlite3.connect(path.as_uri()+"?mode=ro",uri=True)) as db:
        db.execute("PRAGMA query_only=ON")
        tables={}
        for name,key in [("operations","id"),("events","n"),("task_meta","name")]:
            cur=db.execute(f"SELECT * FROM {name} ORDER BY {key}")
            rows=cur.fetchall()
            tables[name] = (len(rows),sha(raw({"columns":[d[0] for d in cur.description],"rows":rows})))
        overlay=db.execute("SELECT decision_id,task_id,operation_id,classification,replay_allowed,production_release "
                           "FROM legacy_adjudication_overlay ORDER BY operation_id").fetchall()
        states=dict(db.execute("SELECT id,state FROM operations"))
        return tables,overlay,states,db.execute("PRAGMA integrity_check").fetchall()


def archive_env(name, expected):
    data=Path(os.environ[name]).read_bytes()
    if sha(data)!=expected:raise ValueError("Archive hash mismatch")
    return ZipFile(io.BytesIO(data))


class Cases(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cases=fixture()

    def test_01_bore_properly_bound(self):
        self.assertEqual(verdict(self.cases[A]), Verdict.VERIFIED_CASE)
        self.assertEqual(verdict(amended(self.cases[A],lambda d:d["decision"].update(transport_outcome="NOT_SENT"))),
                         Verdict.EVIDENCE_MISMATCH)

    def test_02_seat_synthetic_issuance(self):
        self.assertEqual(verdict(self.cases[B]),Verdict.VERIFIED_CASE)
        self.assertEqual(verify_case(self.cases[B][0]),Verdict.SIGNATURE_MISSING)

    @unittest.skipUnless(os.getenv("P13_PENDING_B_ZIP"),"Provide the pinned pending B archive")
    def test_03_authentic_pending_B(self):
        with archive_env("P13_PENDING_B_ZIP","b6010ae2f6e4cb90a0fed7ac7ba4c61a4e0280d1de8254c63912b3535e3fdf2e") as z:
            for name in ("01-用户提交的专项裁决书.md","02-归档说明.json"):
                pending=Material(ISSUED_A.task_id,B,z.read(name),b"",b"",b"")
                outcome=verify_case(pending)
                print("EVIDENCE_PENDING_B "+canonical(dict(member=name,sha256=sha(pending.decision),outcome=outcome.value)))
                self.assertEqual(outcome,Verdict.SIGNATURE_MISSING)
                self.assertEqual(verify_case(pending,fixture_issuance=self.cases[B][1]),Verdict.SIGNATURE_MISSING)

    def test_04_swapped_operation_ids(self):
        for orig,other in [(A,B),(B,A)]:
            m,pin=self.cases[orig]
            self.assertEqual(verdict((replace(m,operation_id=other),pin)),Verdict.EVIDENCE_MISMATCH)

    def test_05_wrong_task(self):
        m,pin=self.cases[B]
        self.assertEqual(verdict((replace(m,task_id="synthetic:other"),pin)),Verdict.EVIDENCE_MISMATCH)
        self.assertEqual(verdict((replace(m,task_spec=raw({"task_id":"synthetic:other"})),pin)),Verdict.EVIDENCE_MISMATCH)

    def test_06_payload_digest(self):
        self.assertEqual(verdict(amended(self.cases[A],lambda d:d["basis"].update(original_payload_digest="0"*64))),Verdict.EVIDENCE_MISMATCH)

    def test_07_receipt_bytes(self):
        m,pin=self.cases[B]
        self.assertEqual(verdict((replace(m,original_receipt=m.original_receipt+b" "),pin)),Verdict.EVIDENCE_MISMATCH)

    def test_08_source_database_sha(self):
        self.assertEqual(verdict(amended(self.cases[B],lambda d:d["evidence_binding"].update(task_database_sha256="0"*64))),Verdict.EVIDENCE_MISMATCH)

    def test_09_each_suppressed(self):
        for name in SUPPRESSED:
            with self.subTest(name=name):
                self.assertEqual(verdict(fixture(changed_constraint=name)[B]),Verdict.SIDE_EFFECT_MISMATCH)

    def test_10_each_fix(self):
        for name in FIXED:
            with self.subTest(name=name):
                self.assertEqual(verdict(fixture(changed_constraint=name)[B]),Verdict.SIDE_EFFECT_MISMATCH)

    def test_11_duplicate_and_changed_decision_id(self):
        pair=self.cases[A]
        alt=amended(pair,lambda d:d.update(decision_id="synthetic-different-id"))
        with isolated_index(pair[0].source_db) as ix:
            self.assertEqual(ix.import_batch([pair]),Verdict.VERIFIED_CASE)
            for duplicated in [pair,alt]:
                self.assertEqual(ix.import_batch([duplicated]),Verdict.EVIDENCE_MISMATCH)
            self.assertEqual(len(inspect_db(ix.path)[1]),1)

    def test_12_missing_empty_malformed_digests(self):
        for key in ["original_application_receipt_sha256","original_payload_digest"]:
            for value in ("","bad",None):
                edit=lambda d:d["basis"].update({key:value})
                with self.subTest(key=key,value=value):
                    self.assertIn(verdict(amended(self.cases[A],edit)),{Verdict.MATERIAL_INCOMPLETE,Verdict.EVIDENCE_MISMATCH})
            self.assertEqual(verdict(amended(self.cases[A],lambda d:d["basis"].pop(key))),Verdict.MATERIAL_INCOMPLETE)

    def test_13_joint_transaction_keeps_history(self):
        a,b=self.cases[A],self.cases[B]
        with isolated_index(a[0].source_db) as ix:
            before=inspect_db(ix.path)
            self.assertEqual(ix.import_batch([a,b]),Verdict.VERIFIED_CASE)
            after=inspect_db(ix.path)
            self.assertEqual(before[0],after[0])
            self.assertEqual(before[2],after[2])
            self.assertEqual([after[2][k] for k in (A,"base.bore.repair1",B)],["failed","succeeded","failed"])
            self.assertEqual([tuple(r[2:]) for r in after[1]],[(A,FAILURE,0,0),(B,PERSISTENT,0,0)])
            self.assertEqual(before[3],after[3])
            self.assertEqual(after[3],[("ok",)])
            print("EVIDENCE_JOINT "+canonical(dict(before=before[0],after=after[0],rows=after[1],integrity="ok")))

    def test_14_unrelated_unknown_blocks(self):
        cases=fixture(extra_unknown=True)
        self.assertEqual(verdict(cases[A]),Verdict.VERIFIED_CASE)
        with isolated_index(cases[A][0].source_db) as ix:
            before=inspect_db(ix.path)
            self.assertEqual(ix.import_batch(list(cases.values())),Verdict.UNKNOWN)
            self.assertEqual(before,inspect_db(ix.path))
            self.assertEqual(before[2]["independent.unknown"],"unknown")
            print("EVIDENCE_UNKNOWN "+canonical(dict(status="UNKNOWN",row=before[2]["independent.unknown"],overlay_rows=len(before[1]),tables=before[0])))

    def test_15_no_global_release(self):
        self.assertEqual(verdict(amended(self.cases[B],lambda d:d["scope"].update(domain_wide_stop_c_release=True))),Verdict.EVIDENCE_MISMATCH)
        self.assertEqual(verdict(amended(self.cases[A],lambda d:d["decision"].update(safe_to_clear_production_stop_c=True))),Verdict.EVIDENCE_MISMATCH)

    def test_16_no_uuid_replay(self):
        spent="6cbdcbf8-f467-48f0-8c71-d39401176b5e"
        for edit in [lambda d:d["scope"].update(attempt_uuid=spent),lambda d:d["scope"].update(historical_command_replay=True),
                     lambda d:d["decision"].update(replay_allowed=True)]:
            self.assertEqual(verdict(amended(self.cases[B],edit)),Verdict.EVIDENCE_MISMATCH)

    def test_17_rollback_on_bad_second_case(self):
        a,b=self.cases[A],self.cases[B]
        broken=(replace(b[0],original_receipt=a[0].original_receipt),b[1])
        with isolated_index(a[0].source_db) as ix:
            before=inspect_db(ix.path)
            self.assertEqual(ix.import_batch([a,broken]),Verdict.EVIDENCE_MISMATCH)
            self.assertEqual(inspect_db(ix.path),before)
            print("EVIDENCE_ROLLBACK "+canonical(dict(rejection="EVIDENCE_MISMATCH",before_rows=len(before[1]),after_rows=len(inspect_db(ix.path)[1]),historical_tables_equal=True)))

    def test_18_mode_invariant_results(self):
        got=[verdict(self.cases[A]).value,verdict(self.cases[B]).value,
             verify_case(self.cases[B][0]).value,verdict((replace(self.cases[A][0],operation_id=B),self.cases[A][1])).value]
        self.assertEqual(got,["VERIFIED_CASE","VERIFIED_CASE","SIGNATURE_MISSING","EVIDENCE_MISMATCH"])
        print("EVIDENCE_VERDICTS "+canonical(got))

    @unittest.skipUnless(os.getenv("P13_CORRECTED_A_ZIP"),"Provide the pinned corrected A archive")
    def test_19_authentic_issued_A(self):
        with archive_env("P13_CORRECTED_A_ZIP","7aa99dcc9a9c13a120d8c6c3343987472445c5f8df76185ad8366e40545ac041") as z:
            doc=z.read("adjudication.json")
            refs=json.loads(doc)["evidence_binding"]
            nested=[n for n in z.namelist() if n.endswith(".zip") and "Investigation" in n]
            self.assertEqual(len(nested),1)
            original_zip=z.read(nested[0])
            self.assertEqual(sha(original_zip),refs["base_evidence_archive_sha256"])
            with ZipFile(io.BytesIO(original_zip)) as src:
                m=Material(ISSUED_A.task_id,A,doc,src.read("historical/task_spec.json"),
                           z.read("source_evidence/original_task_snapshot.sqlite"),
                           src.read(refs["original_receipt_member"]),src.read(refs["repair_receipt_member"]))
            outcome=verify_case(m)
            self.assertEqual(outcome,Verdict.VERIFIED_CASE)
            print("EVIDENCE_ISSUED_A "+canonical(dict(outcome=outcome.value,document=sha(m.decision),source_db=sha(m.source_db),receipt=sha(m.original_receipt))))
            self.assertEqual(verify_case(m,fixture_issuance=self.cases[A][1]),Verdict.SIGNATURE_MISSING)

    def test_20_changes_to_clone_are_detected(self):
        pair=self.cases[A]
        with isolated_index(pair[0].source_db) as ix:
            with closing(sqlite3.connect(ix.path)) as other:
                other.execute("INSERT INTO events(time,kind,data) VALUES (?,?,?)",(stamp(44),"synthetic_change","{}"))
                other.commit()
            self.assertEqual(ix.import_batch([pair]),Verdict.EVIDENCE_MISMATCH)
            self.assertEqual(inspect_db(ix.path)[1],[])

    def test_21_changed_document_with_old_credential(self):
        m,old_pin=self.cases[B]
        changed,_=amended((m,old_pin),lambda d:d.update(decision_id="synthetic-altered"))
        self.assertEqual(verdict((changed,old_pin)),Verdict.SIGNATURE_MISSING)

    def test_22_static_acceptance_requires_affirmative_evidence(self):
        m,pin=self.cases[B]
        acceptance=json.loads(m.static_acceptance)
        acceptance["static_display_accepted"]=False
        m=replace(m,static_acceptance=raw(acceptance))
        doc=json.loads(m.decision)
        doc["basis"]["static_acceptance_sha256"]=sha(m.static_acceptance)
        self.assertEqual(verdict(bind(doc,m)),Verdict.SIDE_EFFECT_MISMATCH)

    def test_23_same_decision_id_across_operations(self):
        a,b=self.cases[A],self.cases[B]
        collision=amended(b,lambda d:d.update(decision_id=json.loads(a[0].decision)["decision_id"]))
        with isolated_index(a[0].source_db) as ix:
            self.assertEqual(ix.import_batch([a,collision]),Verdict.EVIDENCE_MISMATCH)
            self.assertEqual(inspect_db(ix.path)[1],[])
