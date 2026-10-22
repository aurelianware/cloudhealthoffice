package gov.cho.grouper;

import com.google.protobuf.ByteString;
import com.google.protobuf.CodedInputStream;
import com.google.protobuf.Descriptors.FieldDescriptor;
import com.google.protobuf.Message;
import java.io.IOException;
import java.io.InputStream;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.List;

/**
 * Dumps the CMS MS-DRG grouper's own tables out of the Java release, so the
 * .NET grouper is driven by the data CMS actually runs rather than by a
 * transcription of the definitions manual (ADR 024 section 5 step 2).
 *
 * <p>The release ships its tables as a single protobuf buffer,
 * {@code runtime_buffers/msdrg_combined.bin} inside
 * {@code msdrg-binary-access-<v>.jar}, described by the generated
 * {@code gov.agency.msdrg.access.Data} classes. Reading it needs nothing from
 * the release but that jar plus protobuf-java — not the grouper itself, not
 * the 3M foundation library the grouper's runtime options depend on, and no
 * JVM anywhere near production.
 *
 * <p>Each top-level table is written as its own JSON file, keeping protobuf's
 * own field names and nesting, with no reshaping: this tool's job is to make
 * the data readable, and the .NET importer's job is to interpret it. That
 * split keeps the one piece that must track CMS releases small enough to
 * re-read by eye each October.
 *
 * <p>The JSON is written from protobuf's reflection API rather than through
 * {@code JsonFormat}, which would drag in protobuf-java-util, Gson and Guava.
 * Two jars on a classpath is a tool a colleague will actually run; six is one
 * they will not.
 *
 * <p>Usage:
 * <pre>
 *   javac -cp msdrg-binary-access-x.y.z.jar:protobuf-java.jar \
 *         -d classes src/main/java/gov/cho/grouper/ExtractTables.java
 *   java  -Xmx4g -cp classes:msdrg-binary-access-x.y.z.jar:protobuf-java.jar \
 *         gov.cho.grouper.ExtractTables &lt;output-directory&gt;
 * </pre>
 * The binary is read off the classpath, so the version extracted is whichever
 * {@code msdrg-binary-access} jar is on it.
 */
public final class ExtractTables {

  /** Where the release keeps its table buffer inside the jar. */
  private static final String BUFFER_RESOURCE = "runtime_buffers/msdrg_combined.bin";

  /** Companion file recording which build of the data the buffer came from. */
  private static final String METADATA_RESOURCE = "runtime_buffers/msdrg_combined_metadata.txt";

  private ExtractTables() {
  }

  public static void main(String[] args) throws Exception {
    if (args.length != 1) {
      System.err.println("usage: ExtractTables <output-directory>");
      System.exit(2);
    }
    Path out = Paths.get(args[0]);
    Files.createDirectories(out);

    Message root = parseRoot();
    copyMetadata(out);

    // One file per top-level table. Protobuf gives every message a descriptor,
    // so the set of tables is read off the buffer rather than listed here —
    // a table CMS adds in a later version is extracted without a code change.
    List<String> written = new ArrayList<>();
    for (FieldDescriptor field : root.getDescriptorForType().getFields()) {
      if (field.getJavaType() != FieldDescriptor.JavaType.MESSAGE || field.isRepeated()) {
        // The buffer's top level is one singular sub-message per table; anything
        // else is new in this release and wants a human to look at it.
        System.out.printf("skipped %s (%s%s)%n", field.getName(),
            field.getJavaType(), field.isRepeated() ? ", repeated" : "");
        continue;
      }
      Message table = (Message) root.getField(field);
      Path file = out.resolve(field.getName() + ".json");
      try (Writer writer = Files.newBufferedWriter(file, StandardCharsets.UTF_8)) {
        writeMessage(table, writer);
        writer.write('\n');
      }
      written.add(field.getName());
      System.out.printf("%-24s %,12d bytes -> %s%n",
          field.getName(), Files.size(file), file.getFileName());
    }

    System.out.printf("%nextracted %d tables to %s%n", written.size(), out.toAbsolutePath());
  }

  /**
   * Parses the whole buffer. The default protobuf size limit is below the
   * buffer's size, so it is raised explicitly; without this the parse fails
   * with a message about the input being too large rather than a corrupt file.
   */
  private static Message parseRoot() throws IOException {
    try (InputStream in = open(BUFFER_RESOURCE)) {
      CodedInputStream coded = CodedInputStream.newInstance(in);
      coded.setSizeLimit(Integer.MAX_VALUE);
      return gov.agency.msdrg.access.Data.Root.parseFrom(coded);
    }
  }

  /**
   * Copies the release's own build stamp next to the tables. Without it an
   * extracted table set says nothing about which CMS build produced it, and a
   * parity failure months later cannot be pinned to a version.
   */
  private static void copyMetadata(Path out) throws IOException {
    try (InputStream in = ExtractTables.class.getClassLoader()
        .getResourceAsStream(METADATA_RESOURCE)) {
      if (in == null) {
        System.err.println("warning: no " + METADATA_RESOURCE + " on the classpath");
        return;
      }
      Files.copy(in, out.resolve("source-metadata.txt"),
          java.nio.file.StandardCopyOption.REPLACE_EXISTING);
    }
  }

  private static InputStream open(String resource) throws IOException {
    InputStream in = ExtractTables.class.getClassLoader().getResourceAsStream(resource);
    if (in == null) {
      throw new IOException(resource + " is not on the classpath. Put the release's "
          + "msdrg-binary-access jar on it (see this class's javadoc).");
    }
    return in;
  }

  // ─────────────────────────────────────────────────────────
  //  Minimal protobuf-to-JSON writer
  //
  //  Only what this buffer contains: nested messages, maps, repeated
  //  fields, strings, numbers, booleans, enums and bytes. Unset singular
  //  fields are omitted, which keeps the output to the data that is really
  //  there; an empty table therefore writes as {} rather than as a shape.
  // ─────────────────────────────────────────────────────────

  private static void writeMessage(Message message, Writer out) throws IOException {
    out.write('{');
    boolean first = true;
    for (FieldDescriptor field : message.getDescriptorForType().getFields()) {
      if (field.isRepeated()) {
        if (message.getRepeatedFieldCount(field) == 0) {
          continue;
        }
      } else if (!message.hasField(field) && field.hasPresence()) {
        continue;
      }
      if (!first) {
        out.write(',');
      }
      first = false;
      writeString(field.getName(), out);
      out.write(':');
      writeField(field, message.getField(field), out);
    }
    out.write('}');
  }

  private static void writeField(FieldDescriptor field, Object value, Writer out)
      throws IOException {
    if (field.isMapField()) {
      // A protobuf map arrives as a list of key/value messages. Writing it as
      // a JSON object is what makes the dx and pr tables usable: they are maps
      // from a code to its attributes, and a list of pairs would force the
      // importer to rebuild the index.
      out.write('{');
      boolean first = true;
      FieldDescriptor keyField = field.getMessageType().findFieldByName("key");
      FieldDescriptor valueField = field.getMessageType().findFieldByName("value");
      for (Object entry : (List<?>) value) {
        Message pair = (Message) entry;
        if (!first) {
          out.write(',');
        }
        first = false;
        writeString(String.valueOf(pair.getField(keyField)), out);
        out.write(':');
        writeField(valueField, pair.getField(valueField), out);
      }
      out.write('}');
      return;
    }
    if (field.isRepeated()) {
      out.write('[');
      boolean first = true;
      for (Object element : (List<?>) value) {
        if (!first) {
          out.write(',');
        }
        first = false;
        writeScalarOrMessage(field, element, out);
      }
      out.write(']');
      return;
    }
    writeScalarOrMessage(field, value, out);
  }

  private static void writeScalarOrMessage(FieldDescriptor field, Object value, Writer out)
      throws IOException {
    switch (field.getJavaType()) {
      case MESSAGE:
        writeMessage((Message) value, out);
        break;
      case STRING:
        writeString((String) value, out);
        break;
      case ENUM:
        writeString(String.valueOf(value), out);
        break;
      case BOOLEAN:
        out.write(value.toString());
        break;
      case BYTE_STRING:
        // Base64, as protobuf's own JSON mapping does.
        writeString(java.util.Base64.getEncoder()
            .encodeToString(((ByteString) value).toByteArray()), out);
        break;
      case LONG:
        // JSON numbers lose precision past 2^53, so 64-bit values are written
        // as strings — again matching protobuf's JSON mapping.
        writeString(value.toString(), out);
        break;
      default:
        out.write(value.toString());
        break;
    }
  }

  private static void writeString(String value, Writer out) throws IOException {
    out.write('"');
    for (int i = 0; i < value.length(); i++) {
      char c = value.charAt(i);
      switch (c) {
        case '"': out.write("\\\""); break;
        case '\\': out.write("\\\\"); break;
        case '\n': out.write("\\n"); break;
        case '\r': out.write("\\r"); break;
        case '\t': out.write("\\t"); break;
        default:
          if (c < 0x20) {
            out.write(String.format("\\u%04x", (int) c));
          } else {
            out.write(c);
          }
          break;
      }
    }
    out.write('"');
  }
}
