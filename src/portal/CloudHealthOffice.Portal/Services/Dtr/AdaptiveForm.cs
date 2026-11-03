using System.Globalization;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Services.Dtr;

/// <summary>
/// A DTR adaptive form (SDC adaptive questionnaire) answered by hand: the
/// plan asks one question at a time through <c>Questionnaire/$next-question</c>.
///
/// <para>
/// The package gives no questions, only a draft response that contains an
/// empty Questionnaire. Each round sends the response with the answers so
/// far; the plan answers with the same response, its contained Questionnaire
/// holding one more question, until it marks the response completed. The
/// questions asked so far are rendered with <see cref="QuestionnaireForm"/>.
/// </para>
/// </summary>
public sealed class AdaptiveForm
{
    public AdaptiveForm(QuestionnaireResponse response) => Load(response);

    /// <summary>Whether a packaged draft response is for an adaptive form (it names a contained Questionnaire).</summary>
    public static bool IsAdaptiveDraft(QuestionnaireResponse? draft)
        => draft?.Questionnaire is { Length: > 1 } reference && reference.StartsWith('#');

    /// <summary>The response as the plan last returned it.</summary>
    public QuestionnaireResponse Response { get; private set; } = default!;

    /// <summary>The questions asked so far, with the answers given.</summary>
    public QuestionnaireForm Form { get; private set; } = default!;

    /// <summary>The plan has asked everything it needs.</summary>
    public bool Completed => Response.Status == QuestionnaireResponse.QuestionnaireResponseStatus.Completed;

    /// <summary>Takes the plan's answer to the last round.</summary>
    public void Load(QuestionnaireResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var id = response.Questionnaire?.TrimStart('#');
        var contained = response.Contained.OfType<Questionnaire>().FirstOrDefault(q => q.Id == id)
            ?? throw new InvalidOperationException("The adaptive response does not contain the Questionnaire it names.");

        Response = response;
        Form = new QuestionnaireForm(contained);
        Form.ApplyAnswers(response.Item);
    }

    /// <summary>The response to send for the next question: the plan's last response with the answers on the page.</summary>
    public QuestionnaireResponse NextRequest(DateTimeOffset now)
    {
        var next = (QuestionnaireResponse)Response.DeepCopy();
        next.Item = Form.ToResponse(next.Subject, null, now).Item;
        next.Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;
        next.Authored = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return next;
    }
}
