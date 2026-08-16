namespace SynthGen.Core.Support;

/// <summary>Exit codes, stable for scripting and agentic workflows. One scheme for every verb.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Bad input: DDL, rules file, or CLI options.</summary>
    public const int ConfigError = 2;
    /// <summary>Database or unexpected runtime error.</summary>
    public const int RuntimeError = 3;
    /// <summary>Connection is not on the committed allowlist.</summary>
    public const int AllowlistMismatch = 4;
    /// <summary>An identity value collides with the append-only ledger.</summary>
    public const int LedgerConflict = 5;
    /// <summary>Verify layer 1: planned repairs did not close their gaps.</summary>
    public const int GapsRemain = 10;
    /// <summary>Verify layer 2: a rule invariant regressed.</summary>
    public const int InvariantRegression = 20;
    /// <summary>Verify layer 3: a consumer check regressed.</summary>
    public const int ConsumerRegression = 30;
    /// <summary>At least one rules-file evaluation failed (generate/evaluate).</summary>
    public const int EvaluationFailed = 40;
}
