using Hl7.Fhir.Model;

namespace FhirService.Services.Dtr;

/// <summary>The outcome of one <c>Questionnaire/$next-question</c> call.</summary>
public sealed record NextQuestionResult(QuestionnaireResponse? Response, int Status, OperationOutcome? Rejection)
{
    public static NextQuestionResult Ok(QuestionnaireResponse response) => new(response, 200, null);

    public static NextQuestionResult Reject(int status, IEnumerable<OperationOutcome.IssueComponent> issues)
        => new(null, status, new OperationOutcome { Issue = issues.ToList() });
}

/// <summary>
/// DTR adaptive forms (SDC <c>$next-question</c>, DTR 2.2.0).
///
/// <para>
/// An adaptive form is a question bank CHO ships as DTR content
/// (<see cref="DtrContentLibrary.AdaptiveQuestionnaires"/>). What a client
/// receives from <c>$questionnaire-package</c> is the empty search form and a
/// QuestionnaireResponse whose contained Questionnaire holds no questions yet.
/// Each <c>$next-question</c> call validates what the client answered against
/// the questions it was given (spec-147: an invalid response is a 400 with an
/// OperationOutcome) and releases the next question whose
/// <c>enableWhen</c> the answers so far satisfy. When none is left the response
/// is marked <c>completed</c> with a closing display item (spec-39).
/// </para>
///
/// <para>
/// Stateless by design: everything the engine needs is in the response the
/// client sends, and nothing the client sends is stored (DTR sec-10).
/// </para>
/// </summary>
public sealed class AdaptiveQuestionnaireEngine
{
    public const string AdaptiveQuestionnaireProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaire-adapt";

    public const string AdaptiveResponseProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse-adapt";

    /// <summary>The id of the contained Questionnaire in a package's adaptive response.</summary>
    public const string ContainedId = "adaptive";

    /// <summary>The closing display item added when no question remains (spec-39).</summary>
    public const string CompletionLinkId = "cho-complete";

    private readonly DtrContentLibrary _content;

    public AdaptiveQuestionnaireEngine(DtrContentLibrary content) => _content = content;

    /// <summary>
    /// The adaptive QuestionnaireResponse a package carries with an adaptive
    /// form: it references a contained Questionnaire derived from the form's
    /// canonical (spec-25) that holds no questions yet, so the first
    /// <c>$next-question</c> call is the one that asks the first question.
    /// </summary>
    public static Questionnaire EmptyContained(Questionnaire searchForm, string adaptiveUrl)
    {
        var canonical = QuestionnairePackageBuilder.Canonical(searchForm);
        return new Questionnaire
        {
            Id = ContainedId,
            Meta = new Meta { Profile = new[] { AdaptiveQuestionnaireProfile } },
            Extension =
            {
                new Extension(DtrContentLibrary.QuestionnaireAdaptiveExtension, new FhirUrl(adaptiveUrl)),
            },
            Url = searchForm.Url,
            Version = searchForm.Version,
            Name = searchForm.Name,
            Title = searchForm.Title,
            Status = PublicationStatus.Active,
            SubjectTypeElement = searchForm.SubjectTypeElement.Select(e => (Code<ResourceType>)e.DeepCopy()).ToList(),
            DerivedFrom = new[] { canonical },
        };
    }

    /// <summary>Validates <paramref name="response"/> and answers with the next question, or completes it.</summary>
    public NextQuestionResult Next(QuestionnaireResponse response, DateTimeOffset now)
    {
        var issues = new List<OperationOutcome.IssueComponent>();

        var reference = response.Questionnaire;
        if (string.IsNullOrEmpty(reference) || !reference.StartsWith('#'))
        {
            return Invalid("QuestionnaireResponse.questionnaire must reference the contained adaptive Questionnaire " +
                           "(for example '#adaptive').");
        }

        var contained = response.Contained.OfType<Questionnaire>().SingleOrDefault(q => q.Id == reference[1..]);
        if (contained is null)
        {
            return Invalid($"QuestionnaireResponse.questionnaire references '{reference}', but no contained " +
                           "Questionnaire has that id.");
        }

        var bank = Bank(contained);
        if (bank is null)
        {
            return NextQuestionResult.Reject(422, [Issue(OperationOutcome.IssueType.NotFound,
                "The contained Questionnaire is not derived from an adaptive questionnaire this payer publishes " +
                $"({contained.Url}{(contained.Version is { Length: > 0 } v ? "|" + v : "")}).")]);
        }

        var bankItems = bank.Item.ToDictionary(i => i.LinkId, StringComparer.Ordinal);
        var asked = new Dictionary<string, Questionnaire.ItemComponent>(StringComparer.Ordinal);
        foreach (var item in contained.Item.Where(i => i.LinkId != CompletionLinkId))
        {
            if (!bankItems.TryGetValue(item.LinkId ?? "", out var issued) || issued.Type != item.Type)
            {
                issues.Add(Issue(OperationOutcome.IssueType.Invalid,
                    $"The contained Questionnaire holds item '{item.LinkId}', which this payer did not ask."));
                continue;
            }
            asked[issued.LinkId] = issued;
        }

        var answers = new Dictionary<string, DataType>(StringComparer.Ordinal);
        foreach (var answered in response.Item)
        {
            if (!asked.TryGetValue(answered.LinkId ?? "", out var question))
            {
                issues.Add(Issue(OperationOutcome.IssueType.Invalid,
                    $"Item '{answered.LinkId}' answers a question that is not in the contained Questionnaire."));
                continue;
            }

            if (answered.Answer.Count > 1 && question.Repeats != true)
            {
                issues.Add(Issue(OperationOutcome.IssueType.Structure,
                    $"Item '{answered.LinkId}' has {answered.Answer.Count} answers, but the question allows one."));
                continue;
            }

            foreach (var answer in answered.Answer)
            {
                if (AnswerProblem(question, answer.Value) is { } problem)
                {
                    issues.Add(Issue(OperationOutcome.IssueType.Value, $"Item '{answered.LinkId}': {problem}"));
                }
                else
                {
                    answers.TryAdd(question.LinkId, answer.Value);
                }
            }
        }

        foreach (var linkId in answers.Keys.ToList())
        {
            if (!Enabled(asked[linkId], answers))
            {
                issues.Add(Issue(OperationOutcome.IssueType.BusinessRule,
                    $"Item '{linkId}' is answered, but its enableWhen conditions are not met by the other answers."));
            }
        }

        if (issues.Count > 0)
        {
            return NextQuestionResult.Reject(400, issues);
        }

        var next = (QuestionnaireResponse)response.DeepCopy();
        next.Meta ??= new Meta();
        next.Meta.Profile = new[] { AdaptiveResponseProfile };
        next.AuthoredElement = new FhirDateTime(now);
        var nextContained = next.Contained.OfType<Questionnaire>().Single(q => q.Id == contained.Id);

        // A required question that is enabled and still unanswered is the
        // client's to answer first: the response goes back as it came.
        var waiting = asked.Values.Any(q => q.Required == true && Enabled(q, answers) && !answers.ContainsKey(q.LinkId));
        if (waiting)
        {
            next.Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;
            return NextQuestionResult.Ok(next);
        }

        // The next question the answers so far enable. Questions they disable
        // are never asked. A bank question whose conditions name a question
        // not yet answered waits for it.
        var nextQuestion = bank.Item
            .Where(q => !asked.ContainsKey(q.LinkId))
            .FirstOrDefault(q => Enabled(q, answers));

        if (nextQuestion is not null)
        {
            nextContained.Item.Add((Questionnaire.ItemComponent)nextQuestion.DeepCopy());
            next.Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;
            return NextQuestionResult.Ok(next);
        }

        if (nextContained.Item.All(i => i.LinkId != CompletionLinkId))
        {
            nextContained.Item.Add(new Questionnaire.ItemComponent
            {
                LinkId = CompletionLinkId,
                Type = Questionnaire.QuestionnaireItemType.Display,
                Text = "All questions for this request have been answered. Submit this response with the prior " +
                       "authorization request.",
            });
        }
        next.Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed;
        return NextQuestionResult.Ok(next);
    }

    /// <summary>The question bank the contained Questionnaire was derived from.</summary>
    private Questionnaire? Bank(Questionnaire contained)
    {
        foreach (var derived in contained.DerivedFrom)
        {
            if (_content.ResolveAdaptive(derived) is { } bank) return bank;
        }
        return string.IsNullOrEmpty(contained.Url)
            ? null
            : _content.ResolveAdaptive(contained.Version is { Length: > 0 } v ? $"{contained.Url}|{v}" : contained.Url);
    }

    /// <summary>Why <paramref name="value"/> is not an answer <paramref name="question"/> accepts, or null.</summary>
    private static string? AnswerProblem(Questionnaire.ItemComponent question, DataType? value)
    {
        switch (question.Type)
        {
            case Questionnaire.QuestionnaireItemType.Boolean:
                return value is FhirBoolean ? null : $"expected a boolean answer, got {Describe(value)}.";
            case Questionnaire.QuestionnaireItemType.Integer:
                return value is Integer ? null : $"expected an integer answer, got {Describe(value)}.";
            case Questionnaire.QuestionnaireItemType.Decimal:
                return value is FhirDecimal ? null : $"expected a decimal answer, got {Describe(value)}.";
            case Questionnaire.QuestionnaireItemType.String:
            case Questionnaire.QuestionnaireItemType.Text:
                return value is FhirString ? null : $"expected a string answer, got {Describe(value)}.";
            case Questionnaire.QuestionnaireItemType.Date:
                return value is Date ? null : $"expected a date answer, got {Describe(value)}.";
            case Questionnaire.QuestionnaireItemType.Choice:
                if (value is not Coding coding) return $"expected a Coding answer, got {Describe(value)}.";
                return question.AnswerOption.Any(o => o.Value is Coding option
                                                      && option.System == coding.System && option.Code == coding.Code)
                    ? null
                    : $"'{coding.System}|{coding.Code}' is not one of the question's answer options.";
            case Questionnaire.QuestionnaireItemType.Display:
            case Questionnaire.QuestionnaireItemType.Group:
                return "a display or group item takes no answer.";
            default:
                return null;
        }
    }

    private static string Describe(DataType? value) => value?.TypeName ?? "no value";

    /// <summary>
    /// Whether <paramref name="question"/>'s <c>enableWhen</c> holds for the
    /// answers so far. A condition on a question not yet answered does not hold
    /// (except <c>exists = false</c>), so a dependent question waits for it.
    /// </summary>
    private static bool Enabled(Questionnaire.ItemComponent question, IReadOnlyDictionary<string, DataType> answers)
    {
        if (question.EnableWhen.Count == 0) return true;
        var results = question.EnableWhen.Select(condition => Holds(condition, answers)).ToList();
        return question.EnableBehavior == Questionnaire.EnableWhenBehavior.Any ? results.Any(r => r) : results.All(r => r);
    }

    private static bool Holds(Questionnaire.EnableWhenComponent condition, IReadOnlyDictionary<string, DataType> answers)
    {
        var answered = answers.TryGetValue(condition.Question ?? "", out var actual);
        if (condition.Operator == Questionnaire.QuestionnaireItemOperator.Exists)
            return answered == ((condition.Answer as FhirBoolean)?.Value ?? false);
        if (!answered) return false;

        var equal = (actual, condition.Answer) switch
        {
            (FhirBoolean a, FhirBoolean b) => a.Value == b.Value,
            (Coding a, Coding b) => a.System == b.System && a.Code == b.Code,
            (Integer a, Integer b) => a.Value == b.Value,
            (FhirString a, FhirString b) => a.Value == b.Value,
            _ => false,
        };

        return condition.Operator switch
        {
            Questionnaire.QuestionnaireItemOperator.Equal => equal,
            Questionnaire.QuestionnaireItemOperator.NotEqual => !equal,
            Questionnaire.QuestionnaireItemOperator.GreaterThan => Compare(actual, condition.Answer) > 0,
            Questionnaire.QuestionnaireItemOperator.LessThan => Compare(actual, condition.Answer) < 0,
            Questionnaire.QuestionnaireItemOperator.GreaterOrEqual => Compare(actual, condition.Answer) >= 0,
            Questionnaire.QuestionnaireItemOperator.LessOrEqual => Compare(actual, condition.Answer) <= 0,
            _ => false,
        };
    }

    /// <summary>Numeric comparison; anything not numeric compares as "no answer" (never satisfies).</summary>
    private static int? Compare(DataType? a, DataType? b) => (a, b) switch
    {
        (Integer x, Integer y) => x.Value!.Value.CompareTo(y.Value!.Value),
        (FhirDecimal x, FhirDecimal y) => x.Value!.Value.CompareTo(y.Value!.Value),
        _ => null,
    };

    private static NextQuestionResult Invalid(string text)
        => NextQuestionResult.Reject(400, [Issue(OperationOutcome.IssueType.Invalid, text)]);

    private static OperationOutcome.IssueComponent Issue(OperationOutcome.IssueType code, string text) => new()
    {
        Severity = OperationOutcome.IssueSeverity.Error,
        Code = code,
        Diagnostics = text,
    };
}
