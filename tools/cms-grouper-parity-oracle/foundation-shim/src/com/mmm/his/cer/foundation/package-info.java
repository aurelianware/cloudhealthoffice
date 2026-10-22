/**
 * A link-compatibility shim for the Solventum Grouper Foundation Classes
 * (<code>com.mmm.his.cer.gfc.gfc-api:gfc-base-api</code>), which CMS's MS-DRG
 * jars compile against but which CMS does not redistribute and Solventum does
 * not publish to Maven Central.
 *
 * <p>Nothing here was guessed. The CMS jars reference exactly five types from
 * this package, and the required member set was read out of their bytecode
 * constant pools with <code>javap -c</code>:
 *
 * <pre>
 *   Field  GfcPoa.{Y,N,U,W,E,ONE,BLANK,INVALID}:LGfcPoa;
 *   Method GfcPoa.toString:()Ljava/lang/String;
 *   Method ComponentRuntime."&lt;init&gt;":()V
 *   Method Claim."&lt;init&gt;":()V
 *   Method FoundationException."&lt;init&gt;":(Ljava/lang/String;)V
 *   Method FoundationException."&lt;init&gt;":(Ljava/lang/String;Ljava/lang/Throwable;)V
 *   Method FoundationRuntimeException."&lt;init&gt;":(Ljava/lang/String;)V
 * </pre>
 *
 * <p>{@link Processable}, {@link ComponentName}, {@link ComponentPackage},
 * {@link ComponentVersion} and
 * {@link com.mmm.his.cer.foundation.transfer.IClaim} appear only in class
 * signatures, so their shape is fixed by the <code>implements</code> clauses
 * and bridge methods <code>javap</code> prints for
 * <code>MsdrgComponent</code>, <code>MsdrgRuntimeOption</code>,
 * <code>MsdrgClaim</code> and <code>MsdrgFactoryData</code>.
 *
 * <h2>Why this is safe for an oracle</h2>
 *
 * <p>An oracle whose own code decided anything would be worthless. This one
 * decides nothing:
 *
 * <ul>
 *   <li>The CMS jars use no reflective component loading. There is no
 *       <code>Class.forName</code> anywhere in them, so no code-path selection
 *       depends on this shim.</li>
 *   <li>{@link ComponentRuntime} is only ever read through
 *       <code>Map.get</code> and <code>Map.containsKey</code>, so extending
 *       {@link java.util.HashMap} reproduces its full observable behaviour.</li>
 *   <li>{@code GfcPoa.toString()} is called from one place,
 *       <code>TraceUtility</code>, which writes trace text. It does not reach
 *       grouping or HAC logic, so the default enum rendering is immaterial.</li>
 *   <li>Everything else is a constructor, an interface or an exception type.</li>
 * </ul>
 *
 * <p>All MS-DRG logic — MDC assignment, pre-MDC rules, the surgical hierarchy,
 * CC/MCC and CC exclusions, the severity split, the HAC processor — stays in
 * CMS's own compiled classes, driven by CMS's own table buffer. If this shim
 * were wrong in any way that mattered, the jars would fail to link or would
 * throw rather than group quietly to the wrong answer.
 */
package com.mmm.his.cer.foundation;
