"""Read-only classification of two historical NX cases. No live execution entry point."""
from contextlib import closing
from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from hashlib import sha256
import json
import re
import sqlite3

from .task_store import canonical


class Verdict(str, Enum):
    VERIFIED_CASE = "VERIFIED_CASE"
    MATERIAL_INCOMPLETE = "MATERIAL_INCOMPLETE"
    EVIDENCE_MISMATCH = "EVIDENCE_MISMATCH"
    SIDE_EFFECT_MISMATCH = "SIDE_EFFECT_MISMATCH"
    SIGNATURE_MISSING = "SIGNATURE_MISSING"
    UNKNOWN = "UNKNOWN"


A = "base.bore"
B = "fault.conflicting_seat"
FAILURE = "LEGACY_APPLICATION_FAILURE_CONFIRMED"
PERSISTENT = FAILURE + "_WITH_PERSISTENT_SIDE_EFFECTS"
RETAINED = "PERSISTENT_SIDE_EFFECT_CONFIRMED_AND_SUBSEQUENTLY_CORRECTED"
SUPPRESSED = ("conflicting_seat", "seat", "locating_axis")
FIXED = ("fix_pedestal", "fix_base")
TASK = "checkpoint_20261108_192724"


@dataclass(frozen=True)
class Issuance:
    """Independently reviewed pins. Test issuances have a separate task namespace."""
    task_id: str
    operation_id: str
    document_sha256: str
    task_spec_sha256: str
    reference: str


# Pin the original document bytes as accepted in the user-specified corrected A
# archive. Its authority is a conversation reference, not a cryptographic signature.
ISSUED_A = Issuance(TASK, A,
    "cc480e1729606cfd2aef9fb00c56bd522fed72bd8f98a107aaa09e33288e3c38",
    "8253c00d703d35c5c40e469d533e06c106a6528f112216fb9059247b86f11466",
    "NXAI_P13_base_bore_adjudication_corrected_v1.zip/7aa99dcc9a9c13a120d8c6c3343987472445c5f8df76185ad8366e40545ac041")
PENDING_B_NOTE = "f237dd7504ed46e5f551ba8748a2ca0f1fb179a73da018cdb903916a6b54d604"
PENDING_B_BODY = "e75f82351b5769c83afbb191e99ef029be56bb1357ef4c8ea9a06e09f1abf7fc"


@dataclass(frozen=True)
class Material:
    task_id: str                 # Lookup target selected by the reader
    operation_id: str
    decision: bytes
    task_spec: bytes
    source_db: bytes             # Frozen original, never the mutable overlay
    original_receipt: bytes
    repair_receipt: bytes = b""
    static_acceptance: bytes = b""


class Rejected(Exception):
    def __init__(self, status):
        super().__init__(status.value)
        self.status = status


def require(condition, status=Verdict.EVIDENCE_MISMATCH):
    if not condition:
        raise Rejected(status)


def digest(data):
    return sha256(data).hexdigest()


def _pairs(pairs):
    obj = dict(pairs)
    require(len(obj) == len(pairs))
    return obj


def document(data):
    require(isinstance(data, bytes) and data, Verdict.MATERIAL_INCOMPLETE)
    def nonfinite(_):
        raise Rejected(Verdict.EVIDENCE_MISMATCH)
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=_pairs, parse_constant=nonfinite)


def match_hash(expected, data):
    require(expected not in (None, ""), Verdict.MATERIAL_INCOMPLETE)
    require(isinstance(expected, str) and re.fullmatch("[0-9a-f]{64}", expected)
            and expected == digest(data))
    return expected


def required_text(value):
    require(isinstance(value, str) and value.strip(), Verdict.MATERIAL_INCOMPLETE)
    return value


def operation(db, op, state):
    row = db.execute("SELECT kind, state, payload, digest, result FROM operations WHERE id=?", (op,)).fetchone()
    require(row is not None, Verdict.MATERIAL_INCOMPLETE)
    require(row[0] == "nx_command" and row[1] == state)
    payload, result = document(row[2].encode()), document(row[4].encode())
    match_hash(row[3], canonical(payload).encode())
    require(result["ok"] is (state == "succeeded"))
    return payload, row[3], result


def check_receipt(db, op, state, raw, basis, prefix, synthetic):
    receipt = document(raw)
    match_hash(basis[prefix + "_application_receipt_sha256"], raw)
    payload, payload_hash, result = operation(db, op, state)
    match_hash(basis[prefix + "_payload_digest"], canonical(payload).encode())
    require(receipt["operation_id"] == op and receipt["payload_digest"] == payload_hash)
    require(canonical(payload) == canonical(basis[prefix + "_payload"]))
    require(canonical(result) == canonical(receipt["response"]) == canonical(basis[prefix + "_application_response"]))
    require(receipt["provenance"] == ("synthetic application receipt" if synthetic else "actual NX response via MCP"))
    return payload["command"], result


def check_bore(db, material, decision, basis, synthetic, command, response):
    require(command == "nx_hole EXTRUDE(1) 0 0 12.4 8 0"
            and response == {"ok": False, "error": "unknown body_id: EXTRUDE(1)"})
    require(decision["observed_geometry_effect"] == "NO_CHANGE_OBSERVED_BETWEEN_PRE_OPERATION_AND_PRE_REPAIR_INSPECTIONS",
            Verdict.SIDE_EFFECT_MISMATCH)
    require(basis["before_original_inspection_response_sha256"] == basis["before_repair_inspection_response_sha256"]
            and re.fullmatch("[0-9a-f]{64}", basis["before_original_inspection_response_sha256"]),
            Verdict.SIDE_EFFECT_MISMATCH)
    require(basis["repair_operation_id"] == "base.bore.repair1")
    repair, result = check_receipt(db, "base.bore.repair1", "succeeded", material.repair_receipt,
                                  basis, "repair", synthetic)
    require(repair == "nx_hole BODY_1 0 0 12.4 8 0" and result["ok"] is True)

    # The same pre-repair response digest occurs twice. Match time windows as
    # well as response bytes so one observation cannot satisfy both checks.
    original_start, original_end = op_times(db, A)
    repair_start, repair_end = op_times(db, "base.bore.repair1")
    _, reopen_end = op_times(db, "base.verify_open")
    opened, _, reopened = operation(db, "base.verify_open", "succeeded")
    target = required_text(reopened["part"])
    require(opened["command"] == "nx_open_part " + target)
    require(original_end < repair_start and repair_end < reopen_end)
    snapshot_fields = (("before_original", None, original_start),
                       ("before_repair", original_end, repair_start),
                       ("after_repair", repair_end, reopen_end),
                       ("reopened", reopen_end, None))
    found = set()
    for stamp, data in db.execute("SELECT time,data FROM events WHERE kind='nx_observation'"):
        event = document(data.encode())
        if event.get("command") != "nx_inspect":
            continue
        observed = event["response"]
        if observed["ok"] is not True:
            continue
        fingerprint = digest(canonical(observed).encode())
        at = datetime.fromisoformat(stamp)
        for field, lower, upper in snapshot_fields:
            if (lower is None or lower < at) and (upper is None or at < upper):
                if basis[field + "_inspection_response_sha256"] == fingerprint:
                    require(observed["part"] == target and observed["units"] == "Millimeters"
                            and len(observed["bodies"]) == 1, Verdict.SIDE_EFFECT_MISMATCH)
                    solid = observed["bodies"][0]
                    expected = 48000 if field.startswith("before") else 47033.89742716806
                    require(solid["solid"] is True and solid["consistency_fault_count"] == 0
                            and solid["faults"] == [] and abs(solid["volume_mm3"] - expected) < 1e-5,
                            Verdict.SIDE_EFFECT_MISMATCH)
                    found.add(field)
    require(len(found) == len(snapshot_fields), Verdict.SIDE_EFFECT_MISMATCH)


def op_times(db, op):
    row = db.execute("SELECT created,updated FROM operations WHERE id=?", (op,)).fetchone()
    require(row is not None, Verdict.MATERIAL_INCOMPLETE)
    start, end = map(datetime.fromisoformat, row)
    require(start.tzinfo is not None and end.tzinfo is not None and start <= end)
    return start, end


def assembly_observation(db, n):
    require(type(n) is int and n > 0)
    row = db.execute("SELECT time,kind,data FROM events WHERE n=?", (n,)).fetchone()
    require(row is not None, Verdict.MATERIAL_INCOMPLETE)
    event = document(row[2].encode())
    require(row[1] == "nx_observation" and event["command"] == "nx_assembly_state")
    state = event["response"]
    require(state["ok"] is True)
    return row[0], state


def check_seat(db, material, decision, basis, synthetic, command, response):
    bad = Verdict.SIDE_EFFECT_MISMATCH
    require(command == "nx_constraint distance conflicting_seat PEDESTAL 0:1 BASE 0:5 2 opposite")
    require(response["name"] == "conflicting_seat" and response["type"] == "Distance"
            and response["constraint_retained_for_diagnosis"] is True and response["solver_status"] == "OverConstrained", bad)
    require(decision["side_effect_classification"] == RETAINED, bad)
    repair_command, repaired = check_receipt(db, "repair.seat_value", "succeeded", material.repair_receipt,
                                            basis, "repair", synthetic)
    require(basis["repair_operation_id"] == "repair.seat_value" and repair_command == "nx_constraint_value conflicting_seat 0"
            and repaired["name"] == "conflicting_seat" and repaired["solver_status"] == "Solved", bad)
    target = required_text(basis["assembly_path"])
    fault_at, fault = assembly_observation(db, basis["fault_event"])
    retained = [x for x in fault["constraints"] if x["name"] == "conflicting_seat"]
    require(fault["part"] == target and len(retained) == 1 and retained[0]["expression"] == "2"
            and retained[0]["status"] == "OverConstrained" and retained[0]["suppressed"] is False, bad)
    last = op_times(db, B)[1]
    require(last <= datetime.fromisoformat(fault_at), bad)
    last = datetime.fromisoformat(fault_at)
    sequence = ["repair.seat_value", *("assembly.suppress." + n for n in SUPPRESSED),
                "assembly.fix.pedestal", "assembly.save", "assembly.close", "assembly.reopen"]
    for op in sequence:
        start, end = op_times(db, op)
        require(last <= start, bad)
        last = end
    for name in SUPPRESSED:
        payload, _, result = operation(db, "assembly.suppress." + name, "succeeded")
        require(payload["command"] == "nx_constraint_suppress " + name + " 1" and result["name"] == name
                and result["suppressed"] is True and result["solver_status"] == "Suppressed", bad)
    for op, wire, key, value in (
        ("assembly.fix.pedestal", "nx_constraint fix fix_pedestal PEDESTAL component -", "solver_status", "Solved"),
        ("assembly.save", "nx_save_part", "saved", target),
        ("assembly.close", "nx_close_part 0", "result", "closed part"),
        ("assembly.reopen", "nx_open_part " + target, "part", target),
    ):
        payload, _, result = operation(db, op, "succeeded")
        require(payload["command"] == wire and result[key] == value, bad)
    n = basis["reopened_event"]
    at, state = assembly_observation(db, n)
    require(datetime.fromisoformat(at) >= last, bad)
    require(state["part"] == target and type(state["unsatisfied_constraint_count"]) is int
            and state["unsatisfied_constraint_count"] == 0, bad)
    constraints = {row["name"]: row for row in state["constraints"]}
    require(len(constraints) == len(state["constraints"]), bad)
    for name in SUPPRESSED + FIXED:
        row = constraints.get(name)
        require(row is not None, bad)
        suppressed = name in SUPPRESSED
        require(row["suppressed"] is suppressed and row["status"] == ("Suppressed" if suppressed else "Solved"), bad)
        if name in ("seat", "conflicting_seat"):
            require(row["type"] == "Distance" and row["expression"] == "0", bad)
        else:
            require(row["type"] == ("Touch" if suppressed else "Fix"), bad)
    match_hash(basis["static_acceptance_sha256"], material.static_acceptance)
    acceptance = document(material.static_acceptance)
    require(acceptance["task_id"] == material.task_id and acceptance["operation_id"] == B
            and acceptance["part"] == target and acceptance["static_display_accepted"] is True
            and acceptance["suppressed"] == list(SUPPRESSED) and acceptance["solved"] == list(FIXED), bad)


def _verified_row(material, fixture_issuance):
    task, op = material.task_id, material.operation_id
    require(isinstance(task, str) and op in (A, B))
    synthetic = task.startswith("synthetic:") and len(task) > len("synthetic:")
    require(isinstance(material.decision, bytes) and material.decision, Verdict.MATERIAL_INCOMPLETE)
    body_digest = digest(material.decision)
    if body_digest in (PENDING_B_NOTE, PENDING_B_BODY):
        require((task, op) == (TASK, B))
        raise Rejected(Verdict.SIGNATURE_MISSING)
    body = document(material.decision)
    require(isinstance(body, dict))
    body = body.copy()
    body_hash = body.pop("decision_record_canonical_sha256")
    match_hash(body_hash, canonical(body).encode())
    require("attempt_uuid" not in body and "replay_uuid" not in body)
    scope, decision, evidence, basis = (body[k] for k in ("scope", "decision", "evidence_binding", "basis"))
    require((scope["task_id"], scope["operation_id"]) == (task, op))
    require(body["record_type"] == "P13_LEGACY_OPERATION_ADJUDICATION")
    require(body["record_status"] == "ISSUED_FOR_CASE_SPECIFIC_HISTORICAL_CLASSIFICATION", Verdict.SIGNATURE_MISSING)
    approval = fixture_issuance if synthetic else ISSUED_A
    require(approval is not None and (approval.task_id, approval.operation_id) == (task, op), Verdict.SIGNATURE_MISSING)
    require(approval.document_sha256 == body_digest and bool(approval.reference.strip()), Verdict.SIGNATURE_MISSING)
    if synthetic:
        require(approval.reference.startswith("synthetic:"), Verdict.SIGNATURE_MISSING)
    else:
        require(fixture_issuance is None, Verdict.SIGNATURE_MISSING)
    require(scope["only_this_operation"] is True and scope["nx_smoke"] == "NOT_RUN")
    require(all(scope[k] is False for k in ("production_shared_database_write", "domain_wide_stop_c_release", "historical_command_replay")))
    require(set(scope) == {"task_id","operation_id","only_this_operation","nx_smoke","production_shared_database_write",
                            "domain_wide_stop_c_release","historical_command_replay"})
    require(all(decision[k] is False for k in ("replay_allowed", "safe_to_clear_production_stop_c", "production_mutation_authorized")))
    require(decision["transport_outcome"] == "UNKNOWN_NOT_PROVEN" and decision["historical_operation_state_preserved"] == "failed")
    require(decision["application_outcome"] == (FAILURE if op == A else PERSISTENT), Verdict.SIDE_EFFECT_MISMATCH)
    spec = document(material.task_spec)
    match_hash(approval.task_spec_sha256, material.task_spec)
    require(spec["task_id"] == task)
    require(isinstance(material.source_db, bytes) and material.source_db, Verdict.MATERIAL_INCOMPLETE)
    source_hash = match_hash(evidence["task_database_sha256"], material.source_db)
    require(evidence["task_database_bytes"] == len(material.source_db))
    with closing(sqlite3.connect(":memory:")) as db:
        db.deserialize(material.source_db)
        db.execute("PRAGMA locking_mode=exclusive")   # WAL-format archive, private memory only
        db.execute("PRAGMA query_only=ON")
        db.execute("PRAGMA trusted_schema=OFF")
        require(db.execute("PRAGMA integrity_check").fetchall() == [("ok",)], Verdict.UNKNOWN)
        if synthetic:
            require(db.execute("SELECT value FROM task_meta WHERE name='synthetic_task_id'").fetchone() == (task,))
        command, result = check_receipt(db, op, "failed", material.original_receipt, basis, "original", synthetic)
        if op == A:
            check_bore(db, material, decision, basis, synthetic, command, result)
        else:
            check_seat(db, material, decision, basis, synthetic, command, result)
    binding = {"task_id":task, "operation_id":op, "database":source_hash, "spec":approval.task_spec_sha256,
               "payload":basis["original_payload_digest"], "receipt":digest(material.original_receipt),
               "repair_receipt":digest(material.repair_receipt), "document":body_digest, "issuance":approval.reference}
    return (required_text(body["decision_id"]), task, op, decision["application_outcome"], digest(canonical(binding).encode()), body_hash)


def verify_case(material, *, fixture_issuance=None):
    """Return a case verdict only. Synthetic approval pins never authorize real tasks."""
    try:
        _verified_row(material, fixture_issuance)
        return Verdict.VERIFIED_CASE
    except Rejected as error:
        return error.status
    except KeyError:
        return Verdict.MATERIAL_INCOMPLETE
    except (TypeError, ValueError, AttributeError, RecursionError, OverflowError):
        return Verdict.EVIDENCE_MISMATCH
    except (OSError, sqlite3.Error):
        return Verdict.UNKNOWN
