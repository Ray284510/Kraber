"""Classify the evidence for the command that may have changed the part."""
READ_ONLY={'nx_independent_multihole','nx_independent_nurbs_topology','nx_independent_annular_periodic','nx_independent_annular_caps','nx_independent_surface_probe','nx_independent_analytic_boundary','nx_independent_box_boundary','nx_surface_sweep_general_state','nx_mass_convergence_target','nx_surface_spline_state','nx_surface_parametric_state','nx_status','nx_session_parts','nx_inspect','nx_list_bodies','nx_list_features',
           'nx_list_edges','nx_component_geometry','nx_assembly_state','nx_interference',
           'nx_sheet_surface_deviation','nx_surface_refs','nx_mass_convergence','nx_surface_deviation','nx_surface_state','nx_accurate_volume','nx_step_readonly_probe','nx_step_exact_bounds'}


def valid_counts(counts):
    return isinstance(counts,dict) and all(type(n) is int and n>=0 for n in counts.values())


def unsent(receipt):
    if not isinstance(receipt, dict) or receipt.get('outcome') != 'NOT_SENT':
        return False
    counts = receipt.get('counts')
    return (valid_counts(counts) and type(counts.get('WriteFile')) is int
            and counts['WriteFile'] == 0 and type(counts.get('ReadFile')) is int
            and counts['ReadFile'] == 0
            and not receipt.get('cleanup_errors'))


def uncertain(result):
    if not isinstance(result, dict) or type(result.get('ok')) is not bool:
        return True
    receipt = result.get('transport_receipt')
    if (not result['ok'] and result.get('execution_started') is False
            and result.get('retry_safe') is True and result.get('outcome') == 'NOT_SENT'
            and not result.get('response_decode_error') and unsent(receipt)):
        return False
    if (result.get('transport_error') or result.get('response_decode_error')
            or result.get('retry_safe') is True or result.get('outcome') not in (None,'RESPONSE_RECEIVED')
            or (not result['ok'] and result.get('retry_safe') is False)):
        return True
    if receipt is None or not result['ok']:
        return True
    counts = receipt.get('counts') if isinstance(receipt, dict) else None
    return not (valid_counts(counts) and type(counts.get('WriteFile')) is int
                and counts['WriteFile'] == 1 and type(counts.get('ReadFile')) is int
                and counts['ReadFile'] >= 1 and receipt.get('outcome') == 'RESPONSE_RECEIVED'
                and counts.get('CreateFile',1)>=1 and counts.get('CloseHandle',1)==1
                and receipt.get('cleanup_errors')==[] and not receipt.get('error')
                and result.get('execution_started') is not False)

