namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// A CMS table set could not be imported.
///
/// <para>
/// Every one of these is an operator problem, not a claim problem: a missing
/// file, a release that does not carry the version asked for, or a row that
/// contradicts itself. None of them can be recovered from by grouping the stay
/// anyway, because a partially imported table set produces wrong DRGs rather
/// than errors. So the import fails loudly at startup instead.
/// </para>
/// </summary>
public sealed class MsDrgTableImportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MsDrgTableImportException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public MsDrgTableImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
