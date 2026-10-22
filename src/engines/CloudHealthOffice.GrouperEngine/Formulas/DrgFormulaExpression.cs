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
    public bool Evaluate(IReadOnlySet<string> satisfiedOperands) =>
        Evaluate(satisfiedOperands, null);

    /// <summary>
    /// Evaluates the expression, recording into <paramref name="matched"/> every
    /// operand that tested true.
    ///
    /// <para>
    /// This is how a grouping decision becomes explainable: the DRG alone does
    /// not say which diagnosis or procedure earned it, and that is what an
    /// appeal turns on. CMS does the same thing — its <c>ContainsNode</c>
    /// accumulates the attribute into the evaluation context when, and only
    /// when, the lookup succeeds.
    /// </para>
    ///
    /// <para>
    /// Two consequences of matching CMS exactly, both deliberate:
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     <b>Short-circuiting means the record is partial.</b> CMS's
    ///     <c>And</c> and <c>Or</c> are Java <c>&amp;&amp;</c> and
    ///     <c>||</c> — left first, right skipped when the answer is already
    ///     settled. So an operand on the right of a satisfied <c>|</c> never
    ///     tested and never appears here. That is the real evaluation, not an
    ///     omission.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>A negated operand that tested true is still recorded.</b> In
    ///     <c>~a</c>, if <c>a</c> is satisfied the lookup succeeded and CMS
    ///     accumulates it before the negation flips the result. So <c>a</c>
    ///     appears in <paramref name="matched"/> even though the expression
    ///     evaluated false.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="satisfiedOperands">The operands this stay satisfies.</param>
    /// <param name="matched">
    /// Collects the operands that tested true, in evaluation order. May be
    /// <see langword="null"/> when the caller only wants the answer.
    /// </param>
    public abstract bool Evaluate(
        IReadOnlySet<string> satisfiedOperands, ICollection<string>? matched);

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
        public override bool Evaluate(
            IReadOnlySet<string> satisfiedOperands, ICollection<string>? matched)
        {
            if (!satisfiedOperands.Contains(Name))
            {
                return false;
            }

            matched?.Add(Name);
            return true;
        }

        /// <inheritdoc/>
        public override string Print() => Name;
    }

    /// <summary>Logical AND.</summary>
    public sealed record And(DrgFormulaExpression Left, DrgFormulaExpression Right)
        : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(
            IReadOnlySet<string> satisfiedOperands, ICollection<string>? matched) =>
            Left.Evaluate(satisfiedOperands, matched)
            && Right.Evaluate(satisfiedOperands, matched);

        /// <inheritdoc/>
        public override string Print() => $"({Left.Print()} & {Right.Print()})";
    }

    /// <summary>Logical OR.</summary>
    public sealed record Or(DrgFormulaExpression Left, DrgFormulaExpression Right)
        : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(
            IReadOnlySet<string> satisfiedOperands, ICollection<string>? matched) =>
            Left.Evaluate(satisfiedOperands, matched)
            || Right.Evaluate(satisfiedOperands, matched);

        /// <inheritdoc/>
        public override string Print() => $"({Left.Print()} | {Right.Print()})";
    }

    /// <summary>Logical NOT.</summary>
    public sealed record Not(DrgFormulaExpression Inner) : DrgFormulaExpression
    {
        /// <inheritdoc/>
        public override bool Evaluate(
            IReadOnlySet<string> satisfiedOperands, ICollection<string>? matched) =>
            !Inner.Evaluate(satisfiedOperands, matched);

        /// <inheritdoc/>
        public override string Print() => $"~{Inner.Print()}";
    }
}
