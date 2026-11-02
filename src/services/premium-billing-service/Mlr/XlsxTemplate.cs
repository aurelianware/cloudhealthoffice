using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PremiumBillingService.Mlr;

/// <summary>The upload is not an xlsx workbook this code can read, or it is too large once unzipped.</summary>
public sealed class XlsxTemplateException(string message) : Exception(message);

/// <summary>
/// Fills cells in a workbook a state publishes, without a spreadsheet
/// library: the xlsx is opened as a zip, the named sheet's XML is edited in
/// place (each cell keeps its style), and every other part is copied byte for
/// byte, so the state's formulas, protection, validation lists and other
/// sheets stay as the state wrote them. The saved workbook asks Excel to
/// recalculate on open; formula cells this code knows the value of also get
/// that value cached, for viewers that don't recalculate.
/// </summary>
public sealed class XlsxTemplate
{
    /// <summary>Largest workbook accepted, zipped.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Largest total of the parts, unzipped (guards against zip bombs).</summary>
    public const long MaxUnzippedBytes = 100L * 1024 * 1024;

    public const int MaxEntries = 1000;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string WorkbookPath = "xl/workbook.xml";
    private const string WorkbookRelsPath = "xl/_rels/workbook.xml.rels";
    private const string SharedStringsPath = "xl/sharedStrings.xml";

    private readonly List<(string Name, byte[] Content)> _entries;
    private readonly XDocument _workbook;
    private readonly IReadOnlyList<string> _sharedStrings;
    private readonly Dictionary<string, string> _sheetPaths;
    private readonly Dictionary<string, XDocument> _changedSheets = new(StringComparer.Ordinal);

    private XlsxTemplate(List<(string, byte[])> entries, XDocument workbook, IReadOnlyList<string> sharedStrings, Dictionary<string, string> sheetPaths)
    {
        _entries = entries;
        _workbook = workbook;
        _sharedStrings = sharedStrings;
        _sheetPaths = sheetPaths;
    }

    public IReadOnlyCollection<string> SheetNames => _sheetPaths.Keys;

    public static XlsxTemplate Open(byte[] xlsx)
    {
        if (xlsx.Length == 0) throw new XlsxTemplateException("The workbook is empty.");
        if (xlsx.Length > MaxBytes) throw new XlsxTemplateException($"The workbook is larger than {MaxBytes / (1024 * 1024)} MB.");

        var entries = new List<(string, byte[])>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(xlsx, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw new XlsxTemplateException("The workbook has too many parts.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (entry.Length > MaxUnzippedBytes || total > MaxUnzippedBytes)
                    throw new XlsxTemplateException("The workbook is too large once unzipped.");
                using var source = entry.Open();
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (copy.Length + read > entry.Length)
                        throw new XlsxTemplateException("The workbook is not a valid xlsx file.");
                    copy.Write(buffer, 0, read);
                }
                entries.Add((entry.FullName, copy.ToArray()));
            }
        }
        catch (InvalidDataException)
        {
            throw new XlsxTemplateException("The file is not an xlsx workbook.");
        }

        var byName = entries.ToDictionary(e => e.Item1, e => e.Item2, StringComparer.Ordinal);
        if (!byName.TryGetValue(WorkbookPath, out var workbookBytes) || !byName.TryGetValue(WorkbookRelsPath, out var relsBytes))
            throw new XlsxTemplateException("The file is not an xlsx workbook.");

        var workbook = Load(workbookBytes);
        var rels = Load(relsBytes);
        var targets = rels.Root!.Elements(PackageRel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!, StringComparer.Ordinal);

        var sheetPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name");
            var id = (string?)sheet.Attribute(OfficeRel + "id");
            if (name is null || id is null || !targets.TryGetValue(id, out var target)) continue;
            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
            if (byName.ContainsKey(path)) sheetPaths[name] = path;
        }

        var shared = byName.TryGetValue(SharedStringsPath, out var sst)
            ? Load(sst).Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList()
            : [];

        return new XlsxTemplate(entries, workbook, shared, sheetPaths);
    }

    /// <summary>The cell's text as Excel shows it before formatting: shared, inline or literal.</summary>
    public string? Text(string sheet, string cellRef)
    {
        var cell = FindCell(Sheet(sheet), cellRef);
        if (cell is null) return null;
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr") return string.Concat(cell.Element(Main + "is")?.Descendants(Main + "t").Select(t => t.Value) ?? []);
        var value = cell.Element(Main + "v")?.Value;
        if (value is null) return null;
        return type == "s" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < _sharedStrings.Count
            ? _sharedStrings[i]
            : value;
    }

    public string? Formula(string sheet, string cellRef) => FindCell(Sheet(sheet), cellRef)?.Element(Main + "f")?.Value;

    public void SetNumber(string sheet, string cellRef, decimal value)
    {
        var cell = GetOrAddCell(sheet, cellRef);
        cell.Attribute("t")?.Remove();
        cell.Elements().Remove();
        cell.Add(new XElement(Main + "v", Number(value)));
    }

    public void SetText(string sheet, string cellRef, string value)
    {
        var cell = GetOrAddCell(sheet, cellRef);
        cell.SetAttributeValue("t", "inlineStr");
        cell.Elements().Remove();
        cell.Add(new XElement(Main + "is",
            new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
    }

    /// <summary>A date as Excel's serial day number (the cell's own style formats it).</summary>
    public void SetDate(string sheet, string cellRef, DateOnly date) =>
        SetNumber(sheet, cellRef, date.DayNumber - new DateOnly(1899, 12, 30).DayNumber);

    /// <summary>Caches a formula cell's value; the formula itself is left as the state wrote it.</summary>
    public void SetCachedValue(string sheet, string cellRef, decimal value)
    {
        var cell = FindCell(Sheet(sheet), cellRef)
            ?? throw new XlsxTemplateException($"{sheet}!{cellRef} is not in the workbook.");
        if (cell.Element(Main + "f") is null) throw new XlsxTemplateException($"{sheet}!{cellRef} has no formula.");
        _changedSheets[_sheetPaths[sheet]] = Sheet(sheet);
        cell.Attribute("t")?.Remove();
        cell.Element(Main + "v")?.Remove();
        cell.Element(Main + "f")!.AddAfterSelf(new XElement(Main + "v", Number(value)));
    }

    /// <summary>Caches a formula cell's text result (t="str"); the formula is left as it was.</summary>
    public void SetCachedText(string sheet, string cellRef, string value)
    {
        SetCachedValue(sheet, cellRef, 0m);
        var cell = FindCell(Sheet(sheet), cellRef)!;
        cell.SetAttributeValue("t", "str");
        cell.Element(Main + "v")!.Value = value;
    }

    /// <summary>The workbook with the changes, every other part as it was, set to recalculate on open.</summary>
    public byte[] Save()
    {
        var calc = _workbook.Root!.Element(Main + "calcPr");
        if (calc is null)
        {
            calc = new XElement(Main + "calcPr");
            // calcPr follows the last of these that is present (CT_Workbook order).
            var anchor = _workbook.Root.Elements().LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
            if (anchor is null) _workbook.Root.Add(calc); else anchor.AddAfterSelf(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", "1");

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in _entries)
            {
                var bytes = name == WorkbookPath ? Serialize(_workbook)
                    : _changedSheets.TryGetValue(name, out var sheet) ? Serialize(sheet)
                    : content;
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private readonly Dictionary<string, XDocument> _loadedSheets = new(StringComparer.Ordinal);

    private XDocument Sheet(string name)
    {
        if (!_sheetPaths.TryGetValue(name, out var path)) throw new XlsxTemplateException($"The workbook has no sheet named '{name}'.");
        if (!_loadedSheets.TryGetValue(path, out var doc))
        {
            doc = Load(_entries.First(e => e.Name == path).Content);
            _loadedSheets[path] = doc;
        }
        return doc;
    }

    private static XElement? FindCell(XDocument sheet, string cellRef)
    {
        var (_, row) = Split(cellRef);
        return sheet.Root!.Element(Main + "sheetData")?
            .Elements(Main + "row").FirstOrDefault(r => (string?)r.Attribute("r") == row.ToString(CultureInfo.InvariantCulture))?
            .Elements(Main + "c").FirstOrDefault(c => (string?)c.Attribute("r") == cellRef);
    }

    private XElement GetOrAddCell(string sheetName, string cellRef)
    {
        var sheet = Sheet(sheetName);
        _changedSheets[_sheetPaths[sheetName]] = sheet;
        if (FindCell(sheet, cellRef) is { } existing) return existing;

        var (column, rowNumber) = Split(cellRef);
        var data = sheet.Root!.Element(Main + "sheetData") ?? throw new XlsxTemplateException($"'{sheetName}' has no cell data.");
        var row = data.Elements(Main + "row").FirstOrDefault(r => (int?)r.Attribute("r") == rowNumber);
        if (row is null)
        {
            row = new XElement(Main + "row", new XAttribute("r", rowNumber));
            var after = data.Elements(Main + "row").LastOrDefault(r => (int?)r.Attribute("r") < rowNumber);
            if (after is null) data.AddFirst(row); else after.AddAfterSelf(row);
        }

        var cell = new XElement(Main + "c", new XAttribute("r", cellRef));
        var before = row.Elements(Main + "c").LastOrDefault(c => ColumnNumber(Split((string)c.Attribute("r")!).Column) < ColumnNumber(column));
        if (before is null) row.AddFirst(cell); else before.AddAfterSelf(cell);
        return cell;
    }

    private static (string Column, int Row) Split(string cellRef)
    {
        var i = 0;
        while (i < cellRef.Length && char.IsAsciiLetterUpper(cellRef[i])) i++;
        if (i == 0 || !int.TryParse(cellRef.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out var row))
            throw new ArgumentException($"'{cellRef}' is not a cell reference.", nameof(cellRef));
        return (cellRef[..i], row);
    }

    /// <summary>Invariant, without trailing zeros (500000.00 is written 500000).</summary>
    private static string Number(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static int ColumnNumber(string column) => column.Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1);

    private static XDocument Load(byte[] xml)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            throw new XlsxTemplateException("The workbook has a part that is not valid XML.");
        }
    }

    private static byte[] Serialize(XDocument doc)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            doc.Save(writer);
        return output.ToArray();
    }
}
