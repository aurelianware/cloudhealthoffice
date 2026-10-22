package gov.cho.grouper.oracle;

import com.mmm.his.cer.foundation.model.GfcPoa;
import gov.agency.msdrg.model.v2.enumeration.MsdrgSeverity;
import gov.agency.msdrg.model.v2.transfer.input.MsdrgInputDxCode;
import gov.agency.msdrg.v440.enumeration.MsdrgAttributePrefix;
import gov.agency.msdrg.v440.logic.MsdrgSeverityProcessor;
import gov.agency.msdrg.v440.model.Attribute;
import gov.agency.msdrg.v440.model.MsdrgCode.CodeFlag;
import gov.agency.msdrg.v440.model.MsdrgDiagnosisCode;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * Asks CMS what a claim's severity is, for a given formula's suppression list.
 *
 * <p>Severity is recomputed for <b>every</b> formula an MDC holds, with that
 * formula's own suppression list applied, and the answer is then added to the
 * mask as an operand before the formula is evaluated. So the mask the formulas
 * see is not constant. 56 of V44's 824 formulas carry a suppression list.
 *
 * <p>The parts worth pinning rather than assuming: what an empty suppression
 * string does, whether suppression and the CC exclusions are matched loosely,
 * and what a claim with nothing left to count comes to.
 *
 * <pre>
 *   java -cp ... gov.cho.grouper.oracle.SeverityCheck
 * </pre>
 *
 * <p>Its answers are asserted on the .NET side in {@code SeverityTests}.
 */
public final class SeverityCheck {
  private SeverityCheck() {
  }

  private static Attribute parse(String text) {
    int colon = text.indexOf(':');
    if (colon < 0) {
      return new Attribute(text);
    }
    return new Attribute(
        MsdrgAttributePrefix.getPrefixFromString(text.substring(0, colon)),
        text.substring(colon + 1));
  }

  /** A secondary diagnosis with a severity, attributes and optional flags. */
  private static MsdrgDiagnosisCode sdx(
      MsdrgSeverity severity, String attributes, CodeFlag... flags) {
    MsdrgDiagnosisCode code = new MsdrgDiagnosisCode(new MsdrgInputDxCode("Z000", GfcPoa.Y));
    code.setSeverity(severity);

    Set<Attribute> set = new LinkedHashSet<>();
    if (!attributes.isEmpty()) {
      for (String name : attributes.split(",")) {
        set.add(parse(name));
      }
    }
    code.setAttributes(set);

    for (CodeFlag flag : flags) {
      code.mark(flag);
    }

    return code;
  }

  private static void report(
      String question, Set<String> suppression, MsdrgDiagnosisCode... secondaries) {
    MsdrgSeverity severity =
        MsdrgSeverityProcessor.processClaimSeverity(List.of(secondaries), suppression);
    System.out.printf("%-54s | suppress %-22s -> %s%n",
        question, suppression, severity);
  }

  private static Set<String> suppress(String... operands) {
    return new LinkedHashSet<>(List.of(operands));
  }

  public static void main(String[] args) {
    Set<String> none = suppress();
    Set<String> blank = suppress("");

    report("no secondaries at all", none);
    report("one NCC", none, sdx(MsdrgSeverity.NON_CC, "renalfail"));
    report("one CC", none, sdx(MsdrgSeverity.CC, "renalfail"));
    report("a CC and an MCC", none,
        sdx(MsdrgSeverity.CC, "renalfail"), sdx(MsdrgSeverity.MCC, "diabetes"));
    report("an MCC then a CC", none,
        sdx(MsdrgSeverity.MCC, "diabetes"), sdx(MsdrgSeverity.CC, "renalfail"));

    // What CMS's own data writes where a formula suppresses nothing.
    report("one MCC, suppression list is one empty string", blank,
        sdx(MsdrgSeverity.MCC, "diabetes"));

    // The real V44 MDC 0 / DRG 19 suppression list.
    report("the MCC is suppressed, a CC is left",
        suppress("diabetes", "renalfail"),
        sdx(MsdrgSeverity.MCC, "diabetes"), sdx(MsdrgSeverity.CC, "stcc"));
    report("every counting code is suppressed",
        suppress("diabetes", "renalfail"),
        sdx(MsdrgSeverity.MCC, "diabetes"), sdx(MsdrgSeverity.CC, "renalfail"));

    // Is the suppression operand matched loosely?
    report("suppression operand in the wrong case", suppress("DIABETES"),
        sdx(MsdrgSeverity.MCC, "diabetes"));
    report("the code's attribute carries a position prefix", suppress("diabetes"),
        sdx(MsdrgSeverity.MCC, "PDX:diabetes"));
    report("suppression names a list the code does not have", suppress("renalfail"),
        sdx(MsdrgSeverity.MCC, "diabetes"));

    // The two flags other steps set.
    report("the MCC is on the CC exclusion list", none,
        sdx(MsdrgSeverity.MCC, "diabetes", CodeFlag.EXCLUDED),
        sdx(MsdrgSeverity.CC, "stcc"));
    report("the MCC counts only on a stay the patient survived", none,
        sdx(MsdrgSeverity.MCC, "mccalive", CodeFlag.DEATH_EXCLUSION),
        sdx(MsdrgSeverity.CC, "stcc"));
    report("every code is excluded one way or another", none,
        sdx(MsdrgSeverity.MCC, "diabetes", CodeFlag.EXCLUDED),
        sdx(MsdrgSeverity.CC, "mccalive", CodeFlag.DEATH_EXCLUSION));

    // mccalive alone, with no flag set, still counts: the flag is what the
    // discharge status decides, not the attribute.
    report("an mccalive MCC with no death flag", none,
        sdx(MsdrgSeverity.MCC, "mccalive"));
  }
}
