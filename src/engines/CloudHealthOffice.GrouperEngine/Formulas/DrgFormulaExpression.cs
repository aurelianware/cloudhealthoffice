namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// A parsed DRG logic formula: a boolean expression over operand names.
///
/// <para>
/// Evaluation takes the set of operand names a stay satisfies and answers true
/// or false. That is the whole of MS-DRG's branching logic — CMS's own
/// evaluator does exactly this, with each operand name resolved by
/// <c>Collection.contains</c> against the stay's satisfied operands. The work
/// of deciding <i>which</i> operands a stay satisfies belongs to the code
/// attribute tables, not here.
/// </para>
/// </summary>
public abstract record DrgFormulaExpression
{
    /// <summary>
    /// Evaluates the expression against the operand names a stay satisfies.
    /// </summary>
    public abstract bool Evaluate(IReadOnlySet<string> satisfiedOperands);

    /// <summary>
    /// Renders the expression back to formula text. Round-tripping is not
    /// guaranteed to be character-identical — parentheses are normalised — but
    /// it is guaranteed to parse to an equivalent expression, which is what
    /// makes a grouping decision explainable in an audit record.
    /// </summary>
    public abstract string Print();

    /// <summary>An operand name: true when the stay satisfies it.</summary>
    /// <param name="Name">The operand name, as CMS spells it.</param>
    public sealed record Operand(string Name) : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(IReadOnlySet<string> satisfiedOperands) =>
            satisfiedOperands.Contains(Name);

        /// <inheritdoc/>
        public override string Print() => Name;
    }

    /// <summary>Logical AND.</summary>
    public sealed record And(DrgFormulaExpression Left, DrgFormulaExpression Right)
        : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(IReadOnlySet<string> satisfiedOperands) =>
            Left.Evaluate(satisfiedOperands) && Right.Evaluate(satisfiedOperands);

        /// <inheritdoc/>
        public override string Print() => $"({Left.Print()} & {Right.Print()})";
    }

    /// <summary>Logical OR.</summary>
    public sealed record Or(DrgFormulaExpression Left, DrgFormulaExpression Right)
        : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(IReadOnlySet<string> satisfiedOperands) =>
            Left.Evaluate(satisfiedOperands) || Right.Evaluate(satisfiedOperands);

        /// <inheritdoc/>
        public override string Print() => $"({Left.Print()} | {Right.Print()})";
    }

    /// <summary>Logical NOT.</summary>
    public sealed record Not(DrgFormulaExpression Inner) : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(IReadOnlySet<string> satisfiedOperands) =>
            !Inner.Evaluate(satisfiedOperands);

        /// <inheritdoc/>
        public override string Print() => $"~{Inner.Print()}";
    }
}
