using System.Globalization;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Services.Dtr;

// The portal has its own Coverage view model one namespace up.
using Coverage = Hl7.Fhir.Model.Coverage;

/// <summary>
/// A DTR Questionnaire as a form a person fills in by hand: the provider
/// portal's fallback for practices whose EHR has no DTR client.
///
/// <para>
/// Deliberately not a DTR client. It runs no CQL (decision Q-6): the
/// package's Libraries and <c>sdc-questionnaire-initialExpression</c>s are
/// ignored and nothing is prepopulated from an EHR, because there is none.
/// It honours what a person needs to answer the form correctly: item types,
/// <c>required</c>, <c>repeats</c> on choices, <c>answerOption</c> and
/// expanded <c>answerValueSet</c>s, static <c>initial</c> values, and
/// <c>enableWhen</c> (a disabled item is neither required nor answered).
/// </para>
/// </summary>
public sealed class QuestionnaireForm
{
    public const string HiddenExtension = "http://hl7.org/fhir/StructureDefinition/questionnaire-hidden";

    private readonly Dictionary<string, FormItem> _byLinkId = new(StringComparer.Ordinal);

    public QuestionnaireForm(Questionnaire questionnaire, IEnumerable<ValueSet>? valueSets = null)
    {
        Questionnaire = questionnaire ?? throw new ArgumentNullException(nameof(questionnaire));
        var expansions = (valueSets ?? [])
            .Where(v => !string.IsNullOrEmpty(v.Url))
            .GroupBy(v => v.Url, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Items = Build(questionnaire.Item ?? [], parent: null, expansions);
    }

    public Questionnaire Questionnaire { get; }

    public IReadOnlyList<FormItem> Items { get; }

    public string Title => Questionnaire.Title ?? Questionnaire.Name ?? "Documentation questionnaire";

    /// <summary>The version-specific canonical a response names (<c>url|version</c>).</summary>
    public string Canonical => Questionnaire.Version is { Length: > 0 } v
        ? $"{Questionnaire.Url}|{v}"
        : Questionnaire.Url;

    public FormItem? Find(string linkId) => _byLinkId.GetValueOrDefault(linkId);

    /// <summary>All items, depth first.</summary>
    public IEnumerable<FormItem> AllItems => Flatten(Items);

    /// <summary>
    /// The service codes the form is for (its <c>focus</c> use contexts): what
    /// the prior authorization request offers as the requested service.
    /// </summary>
    public IReadOnlyList<Coding> FocusCodes => (Questionnaire.UseContext ?? [])
        .Where(u => u.Code?.Code == "focus")
        .Select(u => u.Value as CodeableConcept)
        .Where(c => c is not null)
        .SelectMany(c => c!.Coding ?? [])
        .Where(c => !string.IsNullOrWhiteSpace(c.Code))
        .ToList();

    /// <summary>
    /// Whether an item is shown and answered: its parent is enabled and its
    /// <c>enableWhen</c> conditions hold (all of them, unless
    /// <c>enableBehavior</c> is <c>any</c>).
    /// </summary>
    public bool IsEnabled(FormItem item)
    {
        if (item.Parent is not null && !IsEnabled(item.Parent))
            return false;

        var conditions = item.Definition.EnableWhen;
        if (conditions is not { Count: > 0 })
            return true;

        var results = conditions.Select(Holds);
        return item.Definition.EnableBehavior == Questionnaire.EnableWhenBehavior.Any
            ? results.Any(r => r)
            : results.All(r => r);
    }

    /// <summary>
    /// What stops the form being submitted, one message per item: a required
    /// enabled question with no answer, or an answer that does not parse.
    /// Empty when the form can be saved as completed.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var item in AllItems.Where(i => i.IsQuestion && IsEnabled(i)))
        {
            if (item.InputError is { } error)
                problems.Add($"{item.Label}: {error}");
            else if (item.Required && item.Supported && !item.HasAnswer)
                problems.Add($"{item.Label}: an answer is required.");
        }
        return problems;
    }

    /// <summary>
    /// The completed response: only enabled items with answers (and groups
    /// that contain some), nested as the Questionnaire nests them.
    /// </summary>
    public QuestionnaireResponse ToResponse(
        ResourceReference subject, IEnumerable<Extension>? extensions, DateTimeOffset authored, ResourceReference? author = null)
    {
        var response = new QuestionnaireResponse
        {
            Questionnaire = Canonical,
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Subject = subject,
            Authored = authored.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Author = author,
            Item = ResponseItems(Items),
        };
        foreach (var extension in extensions ?? [])
            response.Extension.Add((Extension)extension.DeepCopy());
        return response;
    }

    private List<QuestionnaireResponse.ItemComponent> ResponseItems(IEnumerable<FormItem> items)
    {
        var result = new List<QuestionnaireResponse.ItemComponent>();
        foreach (var item in items)
        {
            if (!IsEnabled(item) || item.Type == Questionnaire.QuestionnaireItemType.Display)
                continue;

            var children = ResponseItems(item.Children);
            if (item.Type == Questionnaire.QuestionnaireItemType.Group)
            {
                if (children.Count > 0)
                    result.Add(new QuestionnaireResponse.ItemComponent { LinkId = item.LinkId, Text = item.Definition.Text, Item = children });
                continue;
            }

            var values = item.AnswerValues().ToList();
            if (values.Count == 0)
                continue;

            var answers = values.Select(v => new QuestionnaireResponse.AnswerComponent { Value = v }).ToList();
            // A question's children answer within its (first) answer.
            if (children.Count > 0)
                answers[0].Item = children;
            result.Add(new QuestionnaireResponse.ItemComponent { LinkId = item.LinkId, Text = item.Definition.Text, Answer = answers });
        }
        return result;
    }

    private bool Holds(Questionnaire.EnableWhenComponent condition)
    {
        var question = Find(condition.Question);
        var answers = question is null || !IsEnabled(question) ? [] : question.AnswerValues().ToList();

        if (condition.Operator == Questionnaire.QuestionnaireItemOperator.Exists)
        {
            var wanted = (condition.Answer as FhirBoolean)?.Value ?? true;
            return answers.Count > 0 == wanted;
        }

        if (answers.Count == 0)
            // "!=" against an unanswered question holds: nothing equals it.
            return condition.Operator == Questionnaire.QuestionnaireItemOperator.NotEqual;

        return condition.Operator switch
        {
            Questionnaire.QuestionnaireItemOperator.Equal => answers.Any(a => AnswerEquals(a, condition.Answer)),
            Questionnaire.QuestionnaireItemOperator.NotEqual => !answers.Any(a => AnswerEquals(a, condition.Answer)),
            Questionnaire.QuestionnaireItemOperator.GreaterThan => answers.Any(a => Compare(a, condition.Answer) is int c && c > 0),
            Questionnaire.QuestionnaireItemOperator.LessThan => answers.Any(a => Compare(a, condition.Answer) is int c && c < 0),
            Questionnaire.QuestionnaireItemOperator.GreaterOrEqual => answers.Any(a => Compare(a, condition.Answer) is int c && c >= 0),
            Questionnaire.QuestionnaireItemOperator.LessOrEqual => answers.Any(a => Compare(a, condition.Answer) is int c && c <= 0),
            _ => false,
        };
    }

    internal static bool AnswerEquals(DataType answer, DataType? expected) => (answer, expected) switch
    {
        (Coding a, Coding e) => string.Equals(a.Code, e.Code, StringComparison.Ordinal)
                                && (string.IsNullOrEmpty(e.System) || string.Equals(a.System, e.System, StringComparison.Ordinal)),
        (FhirBoolean a, FhirBoolean e) => a.Value == e.Value,
        (FhirString a, FhirString e) => string.Equals(a.Value, e.Value, StringComparison.Ordinal),
        (FhirString a, Coding e) => string.Equals(a.Value, e.Code, StringComparison.Ordinal),
        _ => Compare(answer, expected) == 0,
    };

    private static int? Compare(DataType answer, DataType? expected) => (answer, expected) switch
    {
        (FhirDecimal a, FhirDecimal e) when a.Value.HasValue && e.Value.HasValue => a.Value.Value.CompareTo(e.Value.Value),
        (FhirDecimal a, Integer e) when a.Value.HasValue && e.Value.HasValue => a.Value.Value.CompareTo((decimal)e.Value.Value),
        (Integer a, Integer e) when a.Value.HasValue && e.Value.HasValue => a.Value.Value.CompareTo(e.Value.Value),
        (Integer a, FhirDecimal e) when a.Value.HasValue && e.Value.HasValue => ((decimal)a.Value.Value).CompareTo(e.Value.Value),
        (Date a, Date e) => string.CompareOrdinal(a.Value, e.Value),
        (FhirDateTime a, FhirDateTime e) => string.CompareOrdinal(a.Value, e.Value),
        (FhirString a, FhirString e) => string.CompareOrdinal(a.Value, e.Value),
        _ => null,
    };

    private List<FormItem> Build(
        IEnumerable<Questionnaire.ItemComponent> definitions, FormItem? parent, Dictionary<string, ValueSet> expansions)
    {
        var items = new List<FormItem>();
        foreach (var definition in definitions)
        {
            if (string.IsNullOrEmpty(definition.LinkId) || IsHidden(definition))
                continue;

            var item = new FormItem(definition, parent, Options(definition, expansions));
            _byLinkId.TryAdd(item.LinkId, item);
            item.Children.AddRange(Build(definition.Item ?? [], item, expansions));
            item.ApplyInitial();
            items.Add(item);
        }
        return items;
    }

    private static bool IsHidden(Questionnaire.ItemComponent definition)
        => definition.Extension?.Any(e => e.Url == HiddenExtension && e.Value is FhirBoolean { Value: true }) == true;

    private static IReadOnlyList<AnswerChoice> Options(
        Questionnaire.ItemComponent definition, Dictionary<string, ValueSet> expansions)
    {
        var options = new List<AnswerChoice>();
        foreach (var option in definition.AnswerOption ?? [])
        {
            if (AnswerChoice.From(option.Value) is { } choice)
                options.Add(choice);
        }

        if (options.Count == 0 && definition.AnswerValueSet is { Length: > 0 } valueSetUrl)
        {
            var url = valueSetUrl.Split('|')[0];
            if (expansions.TryGetValue(url, out var valueSet))
            {
                foreach (var contains in Flatten(valueSet.Expansion?.Contains ?? []))
                {
                    if (string.IsNullOrEmpty(contains.Code) || contains.Abstract == true) continue;
                    options.Add(AnswerChoice.From(new Coding(contains.System, contains.Code, contains.Display))!);
                }
            }
        }

        return options;
    }

    private static IEnumerable<ValueSet.ContainsComponent> Flatten(IEnumerable<ValueSet.ContainsComponent> contains)
        => contains.SelectMany(c => new[] { c }.Concat(Flatten(c.Contains ?? [])));

    private static IEnumerable<FormItem> Flatten(IEnumerable<FormItem> items)
        => items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children)));
}

/// <summary>One choice a question offers, keyed so the page can bind it.</summary>
public sealed record AnswerChoice(string Key, string Display, DataType Value)
{
    public static AnswerChoice? From(DataType? value) => value switch
    {
        Coding c when !string.IsNullOrEmpty(c.Code) => new($"{c.System}|{c.Code}", c.Display ?? c.Code, c),
        FhirString s when !string.IsNullOrEmpty(s.Value) => new($"string:{s.Value}", s.Value, s),
        Integer i when i.Value.HasValue => new($"integer:{i.Value}", i.Value.Value.ToString(CultureInfo.InvariantCulture), i),
        Date d when !string.IsNullOrEmpty(d.Value) => new($"date:{d.Value}", d.Value, d),
        _ => null,
    };
}

/// <summary>
/// One item of the form and the person's answer to it. The page binds the
/// typed properties; <see cref="AnswerValues"/> turns them into FHIR values.
/// </summary>
public sealed class FormItem
{
    internal FormItem(Questionnaire.ItemComponent definition, FormItem? parent, IReadOnlyList<AnswerChoice> options)
    {
        Definition = definition;
        Parent = parent;
        Options = options;
    }

    public Questionnaire.ItemComponent Definition { get; }

    public FormItem? Parent { get; }

    public List<FormItem> Children { get; } = [];

    public IReadOnlyList<AnswerChoice> Options { get; }

    public string LinkId => Definition.LinkId;

    public Questionnaire.QuestionnaireItemType? Type => Definition.Type;

    public string Label => string.IsNullOrWhiteSpace(Definition.Text) ? LinkId : Definition.Text;

    public bool Required => Definition.Required == true;

    public bool Repeats => Definition.Repeats == true;

    public bool ReadOnly => Definition.ReadOnly == true;

    public int? MaxLength => Definition.MaxLength;

    public bool IsQuestion => Type is not (Questionnaire.QuestionnaireItemType.Group or Questionnaire.QuestionnaireItemType.Display);

    /// <summary>The types this page can take an answer for. Others are shown with a note and left unanswered.</summary>
    public bool Supported => Type is Questionnaire.QuestionnaireItemType.Boolean
        or Questionnaire.QuestionnaireItemType.Decimal
        or Questionnaire.QuestionnaireItemType.Integer
        or Questionnaire.QuestionnaireItemType.Date
        or Questionnaire.QuestionnaireItemType.DateTime
        or Questionnaire.QuestionnaireItemType.String
        or Questionnaire.QuestionnaireItemType.Text
        or Questionnaire.QuestionnaireItemType.Url
        or Questionnaire.QuestionnaireItemType.Choice
        or Questionnaire.QuestionnaireItemType.OpenChoice;

    // ── Answer state (bound by the page) ─────────────────────────────────────

    public string? Text { get; set; }

    public bool? Boolean { get; set; }

    /// <summary>Decimal and integer answers as typed, so a bad entry can be reported rather than dropped.</summary>
    public string? Number { get; set; }

    public DateTime? Date { get; set; }

    /// <summary>The selected choice key (single-answer choice).</summary>
    public string? Choice { get; set; }

    /// <summary>The selected choice keys (a choice that repeats).</summary>
    public HashSet<string> Choices { get; } = new(StringComparer.Ordinal);

    public bool HasAnswer => AnswerValues().Any();

    /// <summary>Why the typed answer cannot be used, or null.</summary>
    public string? InputError
    {
        get
        {
            if (MaxLength is { } max && Text?.Trim().Length > max)
                return $"at most {max} characters.";
            if (string.IsNullOrWhiteSpace(Number)) return null;
            return Type switch
            {
                Questionnaire.QuestionnaireItemType.Integer when !int.TryParse(Number.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    => "enter a whole number.",
                Questionnaire.QuestionnaireItemType.Decimal when !decimal.TryParse(Number.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                    => "enter a number.",
                _ => null,
            };
        }
    }

    public IEnumerable<DataType> AnswerValues()
    {
        switch (Type)
        {
            case Questionnaire.QuestionnaireItemType.Boolean:
                if (Boolean.HasValue) yield return new FhirBoolean(Boolean.Value);
                break;
            case Questionnaire.QuestionnaireItemType.Decimal:
                if (decimal.TryParse(Number?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    yield return new FhirDecimal(d);
                break;
            case Questionnaire.QuestionnaireItemType.Integer:
                if (int.TryParse(Number?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    yield return new Integer(i);
                break;
            case Questionnaire.QuestionnaireItemType.Date:
                if (Date.HasValue) yield return new Date(Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case Questionnaire.QuestionnaireItemType.DateTime:
                if (Date.HasValue) yield return new FhirDateTime(Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case Questionnaire.QuestionnaireItemType.String:
            case Questionnaire.QuestionnaireItemType.Text:
                if (!string.IsNullOrWhiteSpace(Text)) yield return new FhirString(Text.Trim());
                break;
            case Questionnaire.QuestionnaireItemType.Url:
                if (!string.IsNullOrWhiteSpace(Text)) yield return new FhirUri(Text.Trim());
                break;
            case Questionnaire.QuestionnaireItemType.Choice:
            case Questionnaire.QuestionnaireItemType.OpenChoice:
                var keys = Repeats ? Choices.ToList() : (Choice is null ? [] : [Choice]);
                // In the Questionnaire's order, not the order clicked.
                foreach (var option in Options.Where(o => keys.Contains(o.Key)))
                    yield return (DataType)option.Value.DeepCopy();
                if (Type == Questionnaire.QuestionnaireItemType.OpenChoice && !string.IsNullOrWhiteSpace(Text))
                    yield return new FhirString(Text.Trim());
                break;
        }
    }

    /// <summary>Static <c>initial</c> values and <c>initialSelected</c> options. Never <c>initialExpression</c> (no CQL here).</summary>
    internal void ApplyInitial()
    {
        foreach (var option in Definition.AnswerOption ?? [])
        {
            if (option.InitialSelected != true || AnswerChoice.From(option.Value) is not { } choice) continue;
            if (Repeats) Choices.Add(choice.Key);
            else Choice ??= choice.Key;
        }

        var initial = Definition.Initial?.FirstOrDefault()?.Value;
        switch (initial)
        {
            case FhirBoolean b: Boolean = b.Value; break;
            case FhirDecimal dec when dec.Value.HasValue: Number = dec.Value.Value.ToString(CultureInfo.InvariantCulture); break;
            case Integer integer when integer.Value.HasValue: Number = integer.Value.Value.ToString(CultureInfo.InvariantCulture); break;
            case Date date when DateTime.TryParse(date.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed): Date = parsed; break;
            case FhirString s: Text = s.Value; break;
            case Coding coding when AnswerChoice.From(coding) is { } choice:
                if (Repeats) Choices.Add(choice.Key);
                else Choice ??= choice.Key;
                break;
        }
    }
}
