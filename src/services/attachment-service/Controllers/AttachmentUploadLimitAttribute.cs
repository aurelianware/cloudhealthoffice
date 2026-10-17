using AttachmentService.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AttachmentService.Controllers;

/// <summary>
/// A <c>[RequestSizeLimit]</c> whose cap comes from configuration
/// (<see cref="AttachmentUploadOptions"/>) instead of a compile-time constant.
/// Runs before model binding, so an oversized body is refused with 413 before
/// it is buffered: a declared Content-Length over the cap is rejected outright,
/// and the server's body-size limit and multipart limit are set to the cap for
/// chunked bodies.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AttachmentUploadLimitAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(serviceProvider.GetRequiredService<IOptions<AttachmentUploadOptions>>().Value);

    private sealed class Filter(AttachmentUploadOptions options) : IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var http = context.HttpContext;
            var cap = options.MaxFileSizeBytes + options.FormOverheadBytes;

            if (http.Request.ContentLength > cap)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = "Attachment too large",
                    Detail = $"Attachments are limited to {options.MaxFileSizeBytes} bytes.",
                })
                { StatusCode = StatusCodes.Status413PayloadTooLarge };
                return;
            }

            var bodySize = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = cap;
            }

            if (http.Features.Get<IFormFeature>()?.Form is null)
            {
                http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions
                {
                    MultipartBodyLengthLimit = cap,
                }));
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}
