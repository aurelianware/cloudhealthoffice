package gov.cho.grouper.oracle;

import gov.agency.msdrg.v440.enumeration.MsdrgAttributePrefix;
import gov.agency.msdrg.v440.logic.ast.AstContext;
import gov.agency.msdrg.v440.logic.ast.MsdrgNodeSupplier;
import gov.agency.msdrg.v440.logic.ast.SuppressionContainsNode;
import gov.agency.msdrg.v440.model.Attribute;
import gov.agency.msdrg.v440.utility.farser.ast.AbstractSyntaxTree;
import gov.agency.msdrg.v440.utility.farser.ast.parser.AstDescentParser;
import gov.agency.msdrg.v440.utility.farser.lexer.DrgFormulaLexer;
import gov.agency.msdrg.v440.utility.farser.lexer.drg.DrgLexerToken;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * Asks CMS's own node supplier what a formula operand actually means, for the
 * operands whose meaning is not set-membership.
 *
 * <p>About a dozen operand names in CMS's data are not lookups. They are
 * "special logic lists" that the node supplier turns into nodes with real
 * behaviour — counting, bilateral pairs, the MCE edits, MDC fall-through — and
 * two consequences of that are impossible to see by reading the formula text:
 *
 * <ul>
 *   <li>The special names are matched <b>case-insensitively</b> and then
 *       <b>normalised</b> to the list's canonical spelling. A formula says
 *       {@code INVALID_PDX}; the attribute the node looks for is
 *       {@code invalid_pdx}. A port that matched the formula's spelling
 *       literally against the mask would never find it.</li>
 *   <li>The lookup is on the operand's list name only, with the <b>position
 *       prefix discarded</b>. So {@code PDX:incident} and {@code SDX:incident}
 *       may not mean what they appear to mean.</li>
 * </ul>
 *
 * <p>Each case below prints the formula, the mask it was evaluated against, and
 * CMS's answer. The .NET side asserts the same answers.
 *
 * <pre>
 *   java -cp ... gov.cho.grouper.oracle.SpecialNodeCheck
 * </pre>
 */
public final class SpecialNodeCheck {
  private SpecialNodeCheck() {
  }

  /** CMS's grouping-path supplier: special nodes, suppression-aware defaults. */
  private static MsdrgNodeSupplier supplier() {
    return new MsdrgNodeSupplier(token -> new SuppressionContainsNode(attribute(token)));
  }

  private static Attribute attribute(DrgLexerToken token) {
    String prefix = token.getPrefix().orElse(MsdrgAttributePrefix.NONE.name());
    return new Attribute(MsdrgAttributePrefix.getPrefixFromString(prefix), token.getValue());
  }

  /** Parses "PDX:incident" style text into one mask attribute. */
  private static Attribute maskEntry(String text) {
    int colon = text.indexOf(':');
    if (colon < 0) {
      return new Attribute(text);
    }
    return new Attribute(
        MsdrgAttributePrefix.getPrefixFromString(text.substring(0, colon)),
        text.substring(colon + 1));
  }

  private static boolean evaluate(String formula, int mdc, String... mask) {
    List<Attribute> attributes = new ArrayList<>();
    for (String entry : mask) {
      attributes.add(maskEntry(entry));
    }

    AstContext context = AstContext.builder()
        .withDiagnosisCodes(Collections.emptyList())
        .withProcedureCodes(Collections.emptyList())
        .withMask(attributes)
        .withMdc(mdc)
        .build();

    List<DrgLexerToken> tokens = DrgFormulaLexer.lex(formula);
    AstDescentParser<DrgLexerToken, ?, AstContext, Boolean> parser =
        new AstDescentParser<>(tokens.iterator(), supplier());
    AbstractSyntaxTree<AstContext, Boolean> tree = parser.buildTree();
    return Boolean.TRUE.equals(tree.evaluateExpression(context).getResult());
  }

  public static void main(String[] args) {
    // {question, formula, mdc, mask entries...}
    String[][] cases = {
      // Does a GRC operand look for the canonical lowercase list name?
      {"INVALID_PDX vs lowercase mask", "INVALID_PDX", "0", "PDX:invalid_pdx"},
      {"INVALID_PDX vs uppercase mask", "INVALID_PDX", "0", "PDX:INVALID_PDX"},
      {"INVALID_PDX vs unprefixed lowercase", "INVALID_PDX", "0", "invalid_pdx"},
      {"INVALID_SEX vs lowercase mask", "INVALID_SEX", "0", "invalid_sex"},
      {"INVALID_SEX vs uppercase mask", "INVALID_SEX", "0", "INVALID_SEX"},
      {"INVALID_DSTAT vs lowercase mask", "INVALID_DSTAT", "0", "invalid_dstat"},
      {"INVALID_DSTAT vs uppercase mask", "INVALID_DSTAT", "0", "INVALID_DSTAT"},
      {"PDX_ECODE vs lowercase mask", "PDX_ECODE", "0", "PDX:pdx_ecode"},
      {"PDX_ECODE vs uppercase mask", "PDX_ECODE", "0", "PDX:PDX_ECODE"},

      // Is the position prefix on a special list name discarded?
      {"PDX:incident vs ONLY in mask", "PDX:incident", "15", "ONLY:incident"},
      {"PDX:incident vs PDX in mask", "PDX:incident", "15", "PDX:incident"},
      {"SDX:incident vs ONLY in mask", "SDX:incident", "15", "ONLY:incident"},
      {"ONLY:incident vs ONLY in mask", "ONLY:incident", "15", "ONLY:incident"},
      {"ONLY:incident vs PDX in mask", "ONLY:incident", "15", "PDX:incident"},

      // An ordinary operand, for contrast: prefix honoured, case exact.
      {"PDX:strauma vs PDX in mask", "PDX:strauma", "24", "PDX:strauma"},
      {"PDX:strauma vs SDX in mask", "PDX:strauma", "24", "SDX:strauma"},
      {"ORPROC vs uppercase mask", "ORPROC", "8", "ORPROC"},
      {"ORPROC vs lowercase mask", "ORPROC", "8", "orproc"},
      {"ANYDX vs uppercase mask", "ANYDX", "15", "ANYDX"},

      // MULTST and BILATERAL need codes, not mask entries: with no codes they
      // must be false however the mask is spelled.
      {"MULTST with the name in the mask", "MULTST", "24", "MULTST"},
      {"BILATERAL with the name in the mask", "BILATERAL", "8", "BILATERAL"},
      {"MDC_20_FALL_THRU with no codes", "MDC_20_FALL_THRU", "20", "MDC_20_FALL_THRU"},
    };

    for (String[] c : cases) {
      String[] mask = Arrays.copyOfRange(c, 3, c.length);
      boolean result;
      try {
        result = evaluate(c[1], Integer.parseInt(c[2]), mask);
        System.out.printf("%-38s | %-18s | mask %-22s -> %s%n",
            c[0], c[1], String.join(",", mask), result);
      } catch (RuntimeException exception) {
        System.out.printf("%-38s | %-18s | mask %-22s -> threw %s%n",
            c[0], c[1], String.join(",", mask), exception);
      }
    }
  }
}
