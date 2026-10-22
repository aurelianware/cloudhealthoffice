package gov.cho.grouper.oracle;

import com.mmm.his.cer.foundation.model.GfcPoa;
import gov.agency.msdrg.model.v2.MsdrgOption;
import gov.agency.msdrg.model.v2.MsdrgRuntimeOption;
import gov.agency.msdrg.model.v2.RuntimeOptions;
import gov.agency.msdrg.model.v2.enumeration.MarkingLogicTieBreaker;
import gov.agency.msdrg.model.v2.enumeration.MsdrgAffectDrgOptionFlag;
import gov.agency.msdrg.model.v2.enumeration.MsdrgDischargeStatus;
import gov.agency.msdrg.model.v2.enumeration.MsdrgHospitalStatusOptionFlag;
import gov.agency.msdrg.model.v2.enumeration.MsdrgSex;
import gov.agency.msdrg.model.v2.transfer.MsdrgClaim;
import gov.agency.msdrg.model.v2.transfer.input.MsdrgInput;
import gov.agency.msdrg.model.v2.transfer.input.MsdrgInputDxCode;
import gov.agency.msdrg.model.v2.transfer.input.MsdrgInputPrCode;
import gov.agency.msdrg.model.v2.transfer.output.MsdrgOutputDxCode;
import gov.agency.msdrg.model.v2.transfer.output.MsdrgOutputData;
import gov.agency.msdrg.v440.MsdrgComponent;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;

/**
 * Runs CMS's own MS-DRG grouper over a reference corpus and writes down what it
 * said, so that CHO's grouper can be held to the same answers.
 *
 * <p>This is the oracle for ADR 024 step 3. It is deliberately dumb: it reads
 * claims, calls {@code MsdrgComponent.process}, and prints the result. It makes
 * no grouping decision of its own and has no opinion about whether CHO is
 * right. The comparison happens on the .NET side, in
 * {@code CmsParityTests}, against the file this writes.
 *
 * <p>Usage:
 *
 * <pre>
 *   java -cp ... gov.cho.grouper.oracle.ParityOracle \
 *       corpus/reference-claims.psv corpus/expected-v44.psv
 * </pre>
 *
 * <p>The corpus is pipe-separated rather than JSON so that it stays readable in
 * a diff and needs no parser dependency; the CMS jars already pin enough of the
 * classpath. Both files carry a header row naming the columns.
 */
public final class ParityOracle {

  /** The grouper version this build of the oracle drives. */
  private static final String VERSION = "V440";

  private ParityOracle() {
  }

  /** Reads the corpus named by the first argument, writes results to the second. */
  public static void main(String[] args) throws Exception {
    if (args.length != 2) {
      System.err.println("usage: ParityOracle <corpus.psv> <expected.psv>");
      System.exit(2);
    }

    List<ReferenceClaim> corpus = readCorpus(Path.of(args[0]));
    System.out.printf("Read %d reference claims from %s%n", corpus.size(), args[0]);

    MsdrgComponent component = newComponent();
    List<String> out = new ArrayList<>();
    out.add("# MS-DRG " + VERSION + " results, produced by tools/cms-grouper-parity-oracle.");
    out.add("# Generated " + LocalDate.now() + ". Do not hand-edit: re-run the oracle.");
    out.add("id|drg|mdc|base_drg|severity|med_surg|grc"
        + "|hac_status|hac_categories|pdx_recognized|sdx_usage");

    int grouped = 0;
    for (ReferenceClaim claim : corpus) {
      Result result = group(component, claim);
      if (result.grc().equals("OK")) {
        grouped++;
      }
      out.add(String.join("|", claim.id(), result.drg(), result.mdc(),
          result.baseDrg(), result.severity(), result.medSurg(), result.grc(),
          result.hacStatus(), result.hacCategories(), result.pdxRecognized(),
          result.sdxUsage()));
    }

    Files.write(Path.of(args[1]), out, StandardCharsets.UTF_8);
    System.out.printf("Wrote %d results to %s (%d grouped OK)%n",
        corpus.size(), args[1], grouped);
  }

  /**
   * Builds the grouper with the option set the API guide (PBL-161) calls for:
   * a non-POA-exempt hospital, affected-DRG computation on, and clinical
   * significance as the marking tie-breaker. These are the defaults CMS
   * documents for a general acute-care IPPS hospital, and CHO's grouper is held
   * to the same ones.
   */
  private static MsdrgComponent newComponent() {
    RuntimeOptions options = new RuntimeOptions();
    options.setPoaReportingExempt(MsdrgHospitalStatusOptionFlag.NON_EXEMPT);
    options.setComputeAffectDrg(MsdrgAffectDrgOptionFlag.COMPUTE);
    options.setMarkingLogicTieBreaker(MarkingLogicTieBreaker.CLINICAL_SIGNIFICANCE);

    MsdrgRuntimeOption runtime = new MsdrgRuntimeOption();
    runtime.put(MsdrgOption.RUNTIME_OPTION_FLAGS, options);
    return new MsdrgComponent(runtime);
  }

  private static Result group(MsdrgComponent component, ReferenceClaim claim) throws Exception {
    List<MsdrgInputDxCode> secondary = new ArrayList<>();
    for (CodedDiagnosis diagnosis : claim.secondaryDiagnoses()) {
      secondary.add(new MsdrgInputDxCode(diagnosis.code(), diagnosis.poa()));
    }

    List<MsdrgInputPrCode> procedures = new ArrayList<>();
    for (String procedure : claim.procedures()) {
      procedures.add(new MsdrgInputPrCode(procedure));
    }

    MsdrgInput input = MsdrgInput.builder()
        .withPrincipalDiagnosisCode(new MsdrgInputDxCode(claim.principalDiagnosis(), GfcPoa.Y))
        .withSecondaryDiagnosisCodes(secondary)
        .withProcedureCodes(procedures)
        .withAgeInYears(claim.ageYears())
        .withAgeDaysAdmit(0)
        .withAgeDaysDischarge(0)
        .withSex(claim.sex())
        .withDischargeStatus(claim.dischargeStatus())
        .build();

    MsdrgClaim msdrgClaim = new MsdrgClaim(input);
    component.process(msdrgClaim);

    Optional<MsdrgOutputData> output = msdrgClaim.getOutput();
    if (output.isEmpty()) {
      return new Result("", "", "", "", "", "NO_OUTPUT", "", "", "", "");
    }

    MsdrgOutputData data = output.get();
    return new Result(
        String.valueOf(data.getFinalDrg().getValue()),
        String.valueOf(data.getFinalMdc().getValue()),
        String.valueOf(data.getFinalBaseDrg().getValue()),
        String.valueOf(data.getFinalSeverity()),
        String.valueOf(data.getFinalMedSugType()),
        String.valueOf(data.getFinalGrc()),
        String.valueOf(data.getHacStatus()),
        String.valueOf(data.getNumHacCategoriesSatisfied()),
        String.valueOf(data.getPdxOutput().isDiagnosisRecognizedByGrouper()),
        describeSeverityUsage(data));
  }

  /**
   * Renders what each secondary diagnosis was actually worth, as
   * {@code CODE:USAGE} pairs.
   *
   * <p>This is the column that makes a parity failure diagnosable. Two
   * groupers can agree on the DRG while disagreeing about which code earned
   * the severity, and a harness that compares only the DRG will call that a
   * pass and then break on the next claim. CMS reports per-code usage
   * directly, so there is no reason to guess.
   */
  private static String describeSeverityUsage(MsdrgOutputData data) {
    List<String> parts = new ArrayList<>();
    for (MsdrgOutputDxCode code : data.getSdxOutput()) {
      parts.add(code.getInputDxCode().getValue() + ":" + code.getFinalSeverityUsage());
    }
    return String.join(",", parts);
  }

  private static List<ReferenceClaim> readCorpus(Path path) throws IOException {
    List<ReferenceClaim> claims = new ArrayList<>();
    for (String raw : Files.readAllLines(path, StandardCharsets.UTF_8)) {
      String line = raw.trim();
      if (line.isEmpty() || line.startsWith("#") || line.startsWith("id|")) {
        continue;
      }
      claims.add(parse(line));
    }
    return claims;
  }

  /**
   * Parses one corpus row:
   * {@code id|pdx|sdx|procedures|age_years|sex|discharge_status|note}, where
   * {@code sdx} is a comma-separated list of {@code CODE:POA} and
   * {@code procedures} is a comma-separated list of codes. Both may be empty.
   */
  private static ReferenceClaim parse(String line) {
    String[] parts = line.split("\\|", -1);
    if (parts.length < 7) {
      throw new IllegalArgumentException("corpus row needs 7 fields: " + line);
    }

    List<CodedDiagnosis> secondary = new ArrayList<>();
    for (String token : splitList(parts[2])) {
      int colon = token.indexOf(':');
      if (colon < 0) {
        throw new IllegalArgumentException("secondary diagnosis needs CODE:POA: " + token);
      }
      secondary.add(new CodedDiagnosis(
          token.substring(0, colon).trim(),
          GfcPoa.valueOf(token.substring(colon + 1).trim())));
    }

    return new ReferenceClaim(
        parts[0].trim(),
        parts[1].trim(),
        secondary,
        splitList(parts[3]),
        Integer.parseInt(parts[4].trim()),
        MsdrgSex.valueOf(parts[5].trim()),
        MsdrgDischargeStatus.valueOf(parts[6].trim()));
  }

  private static List<String> splitList(String field) {
    List<String> values = new ArrayList<>();
    for (String token : field.split(",")) {
      String trimmed = token.trim();
      if (!trimmed.isEmpty()) {
        values.add(trimmed);
      }
    }
    return values;
  }

  private record CodedDiagnosis(String code, GfcPoa poa) {
  }

  private record ReferenceClaim(
      String id,
      String principalDiagnosis,
      List<CodedDiagnosis> secondaryDiagnoses,
      List<String> procedures,
      int ageYears,
      MsdrgSex sex,
      MsdrgDischargeStatus dischargeStatus) {
  }

  private record Result(
      String drg,
      String mdc,
      String baseDrg,
      String severity,
      String medSurg,
      String grc,
      String hacStatus,
      String hacCategories,
      String pdxRecognized,
      String sdxUsage) {
  }
}
