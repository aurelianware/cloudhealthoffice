package gov.cho.grouper.oracle;

import gov.agency.msdrg.v440.utility.farser.ast.AbstractSyntaxTree;
import gov.agency.msdrg.v440.utility.farser.ast.node.supplier.NodeSupplier;
import gov.agency.msdrg.v440.utility.farser.ast.node.terminal.ContainsNode;
import gov.agency.msdrg.v440.utility.farser.ast.node.type.Expression;
import gov.agency.msdrg.v440.utility.farser.ast.parser.AstDescentParser;
import gov.agency.msdrg.v440.utility.farser.lexer.DrgFormulaLexer;
import gov.agency.msdrg.v440.utility.farser.lexer.drg.DrgLexerToken;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Evaluates DRG logic formulas with CMS's own lexer and parser, so that CHO's
 * port of them can be checked against the original rather than against a
 * reading of it.
 *
 * <p>Operator precedence is the part of this language most easily got wrong and
 * least likely to fail loudly: a parser that binds {@code a | b & c} as
 * {@code (a | b) & c} produces a perfectly good boolean answer for every
 * formula, just the wrong one, and only on claims where it matters. CMS's data
 * relies on the precedence rules — {@code "fuse9plus | cmadifd | tulip"} is a
 * real V44 formula with no parentheses at all — so the rules are worth pinning
 * against the source.
 *
 * <p>Every case below is asserted on the .NET side in {@code DrgFormulaTests}.
 * Run this after changing the parser, or on a new fiscal year's jars:
 *
 * <pre>
 *   java -cp ... gov.cho.grouper.oracle.FormulaPrecedenceCheck
 * </pre>
 *
 * <p>The context is a plain {@code Set<String>} of satisfied operand names,
 * which is exactly what CMS's {@code ContainsNode} evaluates against.
 */
public final class FormulaPrecedenceCheck {
  private FormulaPrecedenceCheck() {
  }

  /** Supplies a set-membership node per operand, as CMS's own grouper does. */
  private static final class Supplier implements NodeSupplier<DrgLexerToken, Set<String>> {
    @Override
    public Expression<Set<String>, ?> createNode(DrgLexerToken token) {
      return new ContainsNode<Set<String>, String>(token.getValue());
    }
  }

  public static void main(String[] args) {
    // {formula, comma-separated satisfied operands}
    String[][] cases = {
      {"a | b & c", "b"},
      {"a | b & c", "b,c"},
      {"a | b & c", "a"},
      {"~a & b", "b"},
      {"~a & b", "a,b"},
      {"~a & b", ""},
      {"(a | b) & c", "a"},
      {"fuse9plus | cmadifd | tulip", "tulip"},
      {"(CC & backneck)", "CC"},
      {"(sensor | NORcardcath) & bypass", "sensor"},
      {"(sensor | NORcardcath) & bypass", "sensor,bypass"},
      {"~a | b & ~c", "b"},
      {"~(a & b)", "a"},
      {"a & b | c & d", "b,c"},
    };
    for (String[] c : cases) {
      List<DrgLexerToken> tokens = DrgFormulaLexer.lex(c[0]);
      AstDescentParser<DrgLexerToken, ?, Set<String>, Boolean> parser =
          new AstDescentParser<>(tokens.iterator(), new Supplier());
      AbstractSyntaxTree<Set<String>, Boolean> tree = parser.buildTree();
      Set<String> ctx = new HashSet<>(
          c[1].isEmpty() ? List.of() : Arrays.asList(c[1].split(",")));
      System.out.printf("%-34s | %-14s -> %s%n", c[0], c[1],
          tree.evaluateExpression(ctx).getResult());
    }
  }
}
