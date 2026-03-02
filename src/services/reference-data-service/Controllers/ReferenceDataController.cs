using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ReferenceDataService.Models;
using ReferenceDataService.Repositories;

namespace ReferenceDataService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ReferenceDataController : ControllerBase
{
    private readonly IReferenceDataRepository _referenceDataRepository;
    private readonly ILogger<ReferenceDataController> _logger;
    private readonly IMemoryCache _cache;

    public ReferenceDataController(
        IReferenceDataRepository referenceDataRepository,
        IMemoryCache cache,
        ILogger<ReferenceDataController> logger)
    {
        _referenceDataRepository = referenceDataRepository;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Validate CPT code
    /// CRITICAL for claims processing: ensures procedure code is valid
    /// </summary>
    [HttpGet("cpt/{code}/validate")]
    [ProducesResponseType(typeof(CodeValidationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CodeValidationResponse>> ValidateCptCode(string code)
    {
        _logger.LogInformation("Validating CPT code: {Code}", SanitizeForLog(code));

        var response = await _cache.GetOrCreateAsync($"cpt:{code}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var cptCode = await _referenceDataRepository.GetCptCodeAsync(code);

            return new CodeValidationResponse
            {
                Code = code,
                IsValid = cptCode != null && cptCode.StatusCode == "A",
                CodeType = "CPT",
                Description = cptCode?.ShortDescription,
                Status = cptCode?.StatusCode,
                RequiresPriorAuth = cptCode?.RequiresPriorAuth ?? false,
                ValidationMessage = cptCode == null ? "Code not found" :
                                   cptCode.StatusCode != "A" ? "Code is not active" :
                                   "Code is valid"
            };
        });

        return Ok(response);
    }

    /// <summary>
    /// Validate ICD-10 diagnosis code
    /// CRITICAL for claims processing: ensures diagnosis code is valid and billable
    /// </summary>
    [HttpGet("icd10/{code}/validate")]
    [ProducesResponseType(typeof(CodeValidationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CodeValidationResponse>> ValidateIcd10Code(string code)
    {
        _logger.LogInformation("Validating ICD-10 code: {Code}", SanitizeForLog(code));

        var response = await _cache.GetOrCreateAsync($"icd10:{code}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var icd10Code = await _referenceDataRepository.GetIcd10CodeAsync(code);

            return new CodeValidationResponse
            {
                Code = code,
                IsValid = icd10Code != null && icd10Code.StatusCode == "A" && icd10Code.Billable,
                CodeType = "ICD-10",
                Description = icd10Code?.ShortDescription,
                Status = icd10Code?.StatusCode,
                IsBillable = icd10Code?.Billable,
                ValidationMessage = icd10Code == null ? "Code not found" :
                                   icd10Code.StatusCode != "A" ? "Code is not active" :
                                   !icd10Code.Billable ? "Code is not billable (header code only)" :
                                   "Code is valid"
            };
        });

        return Ok(response);
    }

    /// <summary>
    /// Validate HCPCS code
    /// </summary>
    [HttpGet("hcpcs/{code}/validate")]
    [ProducesResponseType(typeof(CodeValidationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CodeValidationResponse>> ValidateHcpcsCode(string code)
    {
        _logger.LogInformation("Validating HCPCS code: {Code}", SanitizeForLog(code));

        var response = await _cache.GetOrCreateAsync($"hcpcs:{code}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var hcpcsCode = await _referenceDataRepository.GetHcpcsCodeAsync(code);

            return new CodeValidationResponse
            {
                Code = code,
                IsValid = hcpcsCode != null && hcpcsCode.StatusCode == "A",
                CodeType = "HCPCS",
                Description = hcpcsCode?.ShortDescription,
                Status = hcpcsCode?.StatusCode,
                ValidationMessage = hcpcsCode == null ? "Code not found" :
                                   hcpcsCode.StatusCode != "A" ? "Code is not active" :
                                   "Code is valid"
            };
        });

        return Ok(response);
    }

    /// <summary>
    /// Search CPT codes
    /// </summary>
    [HttpGet("cpt/search")]
    [ProducesResponseType(typeof(IEnumerable<CptCode>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CptCode>>> SearchCptCodes(
        [FromQuery] string? searchTerm = null,
        [FromQuery] string? section = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching CPT codes: term={Term}, section={Section}", SanitizeForLog(searchTerm), SanitizeForLog(section));

        var codes = await _referenceDataRepository.SearchCptCodesAsync(searchTerm, section, page, pageSize);
        return Ok(codes);
    }

    /// <summary>
    /// Search ICD-10 codes
    /// </summary>
    [HttpGet("icd10/search")]
    [ProducesResponseType(typeof(IEnumerable<Icd10Code>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Icd10Code>>> SearchIcd10Codes(
        [FromQuery] string? searchTerm = null,
        [FromQuery] string? category = null,
        [FromQuery] bool? billableOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching ICD-10 codes: term={Term}, category={Category}, billable={Billable}",
            SanitizeForLog(searchTerm), SanitizeForLog(category), billableOnly);

        var codes = await _referenceDataRepository.SearchIcd10CodesAsync(searchTerm, category, billableOnly, page, pageSize);
        return Ok(codes);
    }

    /// <summary>
    /// Search HCPCS codes
    /// </summary>
    [HttpGet("hcpcs/search")]
    [ProducesResponseType(typeof(IEnumerable<HcpcsCode>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<HcpcsCode>>> SearchHcpcsCodes(
        [FromQuery] string? searchTerm = null,
        [FromQuery] string? category = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching HCPCS codes: term={Term}, category={Category}", SanitizeForLog(searchTerm), SanitizeForLog(category));

        var codes = await _referenceDataRepository.SearchHcpcsCodesAsync(searchTerm, category, page, pageSize);
        return Ok(codes);
    }

    /// <summary>
    /// Get modifier by code
    /// </summary>
    [HttpGet("modifiers/{code}")]
    [ProducesResponseType(typeof(Modifier), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Modifier>> GetModifier(string code)
    {
        _logger.LogInformation("Fetching modifier: {Code}", SanitizeForLog(code));

        var modifier = await _referenceDataRepository.GetModifierAsync(code);
        if (modifier == null)
        {
            return NotFound($"Modifier {code} not found");
        }

        return Ok(modifier);
    }

    /// <summary>
    /// Get all modifiers
    /// </summary>
    [HttpGet("modifiers")]
    [ProducesResponseType(typeof(IEnumerable<Modifier>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Modifier>>> GetModifiers()
    {
        _logger.LogInformation("Fetching all modifiers");

        var modifiers = await _referenceDataRepository.GetModifiersAsync();
        return Ok(modifiers);
    }

    /// <summary>
    /// Get DRG by code
    /// </summary>
    [HttpGet("drg/{code}")]
    [ProducesResponseType(typeof(DrgCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DrgCode>> GetDrg(string code)
    {
        _logger.LogInformation("Fetching DRG: {Code}", SanitizeForLog(code));

        var drg = await _referenceDataRepository.GetDrgCodeAsync(code);
        if (drg == null)
        {
            return NotFound($"DRG {code} not found");
        }

        return Ok(drg);
    }

    /// <summary>
    /// Search DRG codes
    /// </summary>
    [HttpGet("drg/search")]
    [ProducesResponseType(typeof(IEnumerable<DrgCode>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<DrgCode>>> SearchDrgCodes(
        [FromQuery] string? searchTerm = null,
        [FromQuery] string? mdc = null,
        [FromQuery] int? fiscalYear = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching DRG codes: term={Term}, mdc={MDC}, year={Year}",
            SanitizeForLog(searchTerm), SanitizeForLog(mdc), fiscalYear);

        var codes = await _referenceDataRepository.SearchDrgCodesAsync(searchTerm, mdc, fiscalYear, page, pageSize);
        return Ok(codes);
    }

    /// <summary>
    /// Get place of service by code
    /// </summary>
    [HttpGet("place-of-service/{code}")]
    [ProducesResponseType(typeof(PlaceOfService), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaceOfService>> GetPlaceOfService(string code)
    {
        _logger.LogInformation("Fetching place of service: {Code}", SanitizeForLog(code));

        var pos = await _referenceDataRepository.GetPlaceOfServiceAsync(code);
        if (pos == null)
        {
            return NotFound($"Place of service {code} not found");
        }

        return Ok(pos);
    }

    /// <summary>
    /// Get all places of service
    /// </summary>
    [HttpGet("place-of-service")]
    [ProducesResponseType(typeof(IEnumerable<PlaceOfService>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PlaceOfService>>> GetPlacesOfService()
    {
        _logger.LogInformation("Fetching all places of service");

        var places = await _referenceDataRepository.GetPlacesOfServiceAsync();
        return Ok(places);
    }

    /// <summary>
    /// Get revenue code by code
    /// </summary>
    [HttpGet("revenue/{code}")]
    [ProducesResponseType(typeof(RevenueCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RevenueCode>> GetRevenueCode(string code)
    {
        _logger.LogInformation("Fetching revenue code: {Code}", SanitizeForLog(code));

        var revenueCode = await _referenceDataRepository.GetRevenueCodeAsync(code);
        if (revenueCode == null)
        {
            return NotFound($"Revenue code {code} not found");
        }

        return Ok(revenueCode);
    }

    /// <summary>
    /// Search revenue codes
    /// </summary>
    [HttpGet("revenue/search")]
    [ProducesResponseType(typeof(IEnumerable<RevenueCode>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RevenueCode>>> SearchRevenueCodes(
        [FromQuery] string? searchTerm = null,
        [FromQuery] string? category = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching revenue codes: term={Term}, category={Category}", SanitizeForLog(searchTerm), SanitizeForLog(category));

        var codes = await _referenceDataRepository.SearchRevenueCodesAsync(searchTerm, category, page, pageSize);
        return Ok(codes);
    }

    /// <summary>
    /// Get reference data statistics
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(ReferenceDataStats), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReferenceDataStats>> GetStats()
    {
        _logger.LogInformation("Fetching reference data statistics");

        var stats = await _referenceDataRepository.GetStatsAsync();
        return Ok(stats);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
