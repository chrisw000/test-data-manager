namespace Tdm.Core.Execution;

/// <summary>Thrown under FailRun policy — aborts the whole run.</summary>
public sealed class TdmRunAbortedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Thrown under FailObject policy — the current object is rejected, the scenario continues.</summary>
public sealed class TdmObjectRejectedException(string message, Exception? inner = null)
    : Exception(message, inner);
