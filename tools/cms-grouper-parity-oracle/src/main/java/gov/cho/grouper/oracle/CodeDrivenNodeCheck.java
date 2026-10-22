package gov.cho.grouper.oracle;

import com.mmm.his.cer.foundation.model.GfcPoa;
import gov.agency.msdrg.model.v2.transfer.input.MsdrgInputDxCode;
import gov.agency.msdrg.model.v2.transfer.input.MsdrgInputPrCode;
import gov.agency.msdrg.v440.enumeration.MsdrgAttributePrefix;
import gov.agency.msdrg.v440.logic.ast.AstContext;
import gov.agency.msdrg.v440.logic.ast.MsdrgNodeSupplier;
import gov.agency.msdrg.v440.logic.ast.SuppressionContainsNode;
import gov.agency.msdrg.v440.model.Attribute;
import gov.agency.msdrg.v440.model.MsdrgDiagnosisCode;
import gov.agency.msdrg.v440.model.MsdrgProcedureCode;
import gov.agency.msdrg.v440.utility.farser.ast.AbstractSyntaxTree;
import gov.agency.msdrg.v440.utility.farser.ast.parser.AstDescentParser;
import gov.agency.msdrg.v440.utility.farser.lexer.DrgFormulaLexer;
import gov.agency.msdrg.v440.utility.farser.lexer.drg.DrgLexerToken;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * Asks CMS's own grouper what the code-driven operands mean.
 *
 * <p>{@link SpecialNodeCheck} covers the special operands that read the
 * attribute mask. The eleven here do not: they walk the claim's diagnosis or
 * procedure list and count. Nothing in the formula text says so, and each one
 * is the whole condition of the formula it sits in, so getting one wrong moves
 * a stay to a different DRG silently.
 *
 * <p>Several of their rules are not what the names suggest. {@code MULTST} and
 * {@code BILATERAL} take at most one attribute per code, so a single code
 * carrying both halves of a pair does not satisfy them. {@code BILATERAL} is
 * satisfied by any two of its six lists, so a right hip and a right knee count.
 * {@code MULTFUSE} has no distinctness requirement at all where
 * {@code SINGLEFUSIONCTR} does. The counting nodes weight their attributes, so
 * one {@code vessel4} is worth four. Each of those is printed below.
 *
 * <pre>
 *   java -cp ... gov.cho.grouper.oracle.CodeDrivenNodeCheck
 * </pre>
 *
 * <p>Its answers are asserted on the .NET side in {@code CodeDrivenNodeTests}.
 */
public final class CodeDrivenNodeCheck {
  private CodeDrivenNodeCheck() {
  }

  /** CMS's grouping-path supplier: special nodes, suppression-aware defaults. */
  private static MsdrgNodeSupplier supplier() {
    return new MsdrgNodeSupplier(token -> new SuppressionContainsNode(attribute(token)));
  }

  private static Attribute attribute(DrgLexerToken token) {
    String prefix = token.getPrefix().orElse(MsdrgAttributePrefix.NONE.name());
    return new Attribute(MsdrgAttributePrefix.getPrefixFromString(prefix), token.getValue());
  }

  /** Parses "PDX:sthead" style text into one attribute. */
  private static Attribute parse(String text) {
    int colon = text.indexOf(':');
    if (colon < 0) {
      return new Attribute(text);
    }
    return new Attribute(
        MsdrgAttributePrefix.getPrefixFromString(text.substring(0, colon)),
        text.substring(colon + 1));
  }

  private static Set<Attribute> attributes(String... names) {
    Set<Attribute> set = new LinkedHashSet<>();
    for (String name : names) {
      set.add(parse(name));
    }
    return set;
  }

  /** A diagnosis carrying the given attributes, assigned to the given MDC. */
  private static MsdrgDiagnosisCode dx(int mdc, String... names) {
    MsdrgDiagnosisCode code = new MsdrgDiagnosisCode(new MsdrgInputDxCode("Z000", GfcPoa.Y));
    code.setMdc(mdc);
    code.setAttributes(attributes(names));
    return code;
  }

  /** A procedure carrying the given attributes. */
  private static MsdrgProcedureCode pr(String... names) {
    MsdrgProcedureCode code = new MsdrgProcedureCode(new MsdrgInputPrCode("0JH00ZZ"));
    code.setAttributes(attributes(names));
    return code;
  }

  private static boolean evaluate(
      String formula,
      List<MsdrgDiagnosisCode> diagnoses,
      List<MsdrgProcedureCode> procedures,
      int mdc) {
    AstContext context = AstContext.builder()
        .withDiagnosisCodes(diagnoses)
        .withProcedureCodes(procedures)
        .withMask(new ArrayList<>())
        .withMdc(mdc)
        .build();

    List<DrgLexerToken> tokens = DrgFormulaLexer.lex(formula);
    AstDescentParser<DrgLexerToken, ?, AstContext, Boolean> parser =
        new AstDescentParser<>(tokens.iterator(), supplier());
    AbstractSyntaxTree<AstContext, Boolean> tree = parser.buildTree();
    return Boolean.TRUE.equals(tree.evaluateExpression(context).getResult());
  }

  private static void dxCase(String question, String formula, MsdrgDiagnosisCode... diagnoses) {
    report(question, formula, List.of(diagnoses), Collections.emptyList());
  }

  private static void prCase(String question, String formula, MsdrgProcedureCode... procedures) {
    report(question, formula, Collections.emptyList(), List.of(procedures));
  }

  private static void report(
      String question,
      String formula,
      List<MsdrgDiagnosisCode> diagnoses,
      List<MsdrgProcedureCode> procedures) {
    try {
      // MDC 0 throughout: none of these nodes reads the MDC.
      boolean result = evaluate(formula, diagnoses, procedures, 0);
      System.out.printf("%-58s | %-18s -> %s%n", question, formula, result);
    } catch (RuntimeException exception) {
      System.out.printf("%-58s | %-18s -> threw %s%n", question, formula, exception);
    }
  }

  public static void main(String[] args) {
    System.out.println("-- MULTST: significant trauma, over the diagnoses");
    dxCase("one trauma list only", "MULTST", dx(24, "sthead"));
    dxCase("two codes, two different lists", "MULTST",
        dx(24, "sthead"), dx(24, "stchest"));
    dxCase("two codes, the same list twice", "MULTST",
        dx(24, "sthead"), dx(24, "sthead"));
    dxCase("ONE code carrying two different lists", "MULTST",
        dx(24, "sthead", "stchest"));
    dxCase("the duplicate first, then a second list", "MULTST",
        dx(24, "sthead"), dx(24, "sthead"), dx(24, "stchest"));
    dxCase("the lists reversed against enum order", "MULTST",
        dx(24, "stlolimb"), dx(24, "sthead"));
    dxCase("a list name in the wrong case", "MULTST",
        dx(24, "STHEAD"), dx(24, "stchest"));
    dxCase("list names carrying a position prefix", "MULTST",
        dx(24, "PDX:sthead"), dx(24, "SDX:stchest"));
    dxCase("the operand name in the wrong case", "multst",
        dx(24, "sthead"), dx(24, "stchest"));
    dxCase("the operand name with a position prefix", "PDX:MULTST",
        dx(24, "sthead"), dx(24, "stchest"));
    dxCase("no diagnoses at all", "MULTST");

    System.out.println();
    System.out.println("-- BILATERAL: over the procedures");
    prCase("one side only", "BILATERAL", pr("righthip"));
    prCase("a matched left/right pair", "BILATERAL",
        pr("righthip"), pr("lefthip"));
    prCase("ONE code carrying both sides", "BILATERAL",
        pr("righthip", "lefthip"));
    prCase("two lists on the SAME side", "BILATERAL",
        pr("righthip"), pr("rightknee"));
    prCase("the same list twice", "BILATERAL",
        pr("righthip"), pr("righthip"));
    prCase("a list name in the wrong case", "BILATERAL",
        pr("RIGHTHIP"), pr("lefthip"));
    prCase("a list name carrying a position prefix", "BILATERAL",
        pr("PDX:righthip"), pr("lefthip"));

    System.out.println();
    System.out.println("-- SINGLEFUSIONCTR: both lists, three occurrences");
    prCase("both lists, two occurrences", "SINGLEFUSIONCTR",
        pr("sglantfuse"), pr("sglpostfuse"));
    prCase("both lists, three occurrences", "SINGLEFUSIONCTR",
        pr("sglantfuse"), pr("sglantfuse"), pr("sglpostfuse"));
    prCase("three occurrences, one code carries two", "SINGLEFUSIONCTR",
        pr("sglantfuse", "sglpostfuse"), pr("sglantfuse"));
    prCase("one list, three occurrences", "SINGLEFUSIONCTR",
        pr("sglantfuse"), pr("sglantfuse"), pr("sglantfuse"));

    System.out.println();
    System.out.println("-- MULTFUSE: two occurrences, no distinctness");
    prCase("one occurrence", "MULTFUSE", pr("sglantfuse"));
    prCase("the same list twice", "MULTFUSE",
        pr("sglantfuse"), pr("sglantfuse"));
    prCase("ONE code carrying both lists", "MULTFUSE",
        pr("sglantfuse", "sglpostfuse1"));
    prCase("twice, but the list is not in this enum", "MULTFUSE",
        pr("sglpostfuse"), pr("sglpostfuse"));

    System.out.println();
    System.out.println("-- The single-list counters");
    prCase("SGLANTCTR once", "SGLANTCTR", pr("sglantfuse"));
    prCase("SGLANTCTR twice", "SGLANTCTR",
        pr("sglantfuse"), pr("sglantfuse"));
    prCase("SGLPOST1CTR twice on sglpostfuse1", "SGLPOST1CTR",
        pr("sglpostfuse1"), pr("sglpostfuse1"));
    prCase("SGLPOST1CTR twice on sglpostfuse", "SGLPOST1CTR",
        pr("sglpostfuse"), pr("sglpostfuse"));
    // Two of CMS's enum constants are not named after the operand they match:
    // SGLANTFUSECTR's value is "SGLANTCTR" and SGLPOST1FUSECTR's is
    // "SGLPOST1CTR". The Java names are therefore ordinary operands, which
    // means a plain mask lookup -- and the mask here is empty.
    prCase("the enum's Java name, not the operand", "SGLANTFUSECTR",
        pr("sglantfuse"), pr("sglantfuse"));
    prCase("the enum's Java name, not the operand", "SGLPOST1FUSECTR",
        pr("sglpostfuse1"), pr("sglpostfuse1"));
    prCase("SGLPOSTFUSECTR twice on sglpostfuse", "SGLPOSTFUSECTR",
        pr("sglpostfuse"), pr("sglpostfuse"));
    prCase("SGLANTSECTXCTR twice, capital X", "SGLANTSECTXCTR",
        pr("sglantsectXfuse"), pr("sglantsectXfuse"));
    prCase("SGLANTSECTXCTR twice, lower-case x", "SGLANTSECTXCTR",
        pr("sglantsectxfuse"), pr("sglantsectxfuse"));

    System.out.println();
    System.out.println("-- FOUR_VESSELS / FOUR_STENTS: the counts are weighted");
    prCase("one vessel4", "FOUR_VESSELS", pr("vessel4"));
    prCase("one vessel3", "FOUR_VESSELS", pr("vessel3"));
    prCase("two vessel2", "FOUR_VESSELS", pr("vessel2"), pr("vessel2"));
    prCase("four vessel1", "FOUR_VESSELS",
        pr("vessel1"), pr("vessel1"), pr("vessel1"), pr("vessel1"));
    prCase("three vessel1", "FOUR_VESSELS",
        pr("vessel1"), pr("vessel1"), pr("vessel1"));
    prCase("ONE code carrying vessel1 and vessel3", "FOUR_VESSELS",
        pr("vessel1", "vessel3"));
    prCase("one stent4", "FOUR_STENTS", pr("stent4"));
    prCase("three stent1", "FOUR_STENTS",
        pr("stent1"), pr("stent1"), pr("stent1"));
    prCase("a stent operand against vessel codes", "FOUR_STENTS", pr("vessel4"));

    System.out.println();
    System.out.println("-- MDC_20_FALL_THRU: the principal diagnosis only");
    dxCase("principal in MDC 20", "MDC_20_FALL_THRU", dx(20, "x"), dx(5, "x"));
    dxCase("MDC 20 on a secondary", "MDC_20_FALL_THRU", dx(5, "x"), dx(20, "x"));
    dxCase("no diagnoses at all", "MDC_20_FALL_THRU");
  }
}
