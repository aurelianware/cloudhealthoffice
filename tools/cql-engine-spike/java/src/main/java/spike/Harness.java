package spike;

import ca.uhn.fhir.context.FhirContext;
import ca.uhn.fhir.parser.IParser;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.time.ZoneOffset;
import java.time.ZonedDateTime;
import java.util.*;
import org.hl7.fhir.r4.model.*;
import org.opencds.cqf.fhir.cql.engine.retrieve.RetrieveSettings.SEARCH_FILTER_MODE;
import org.opencds.cqf.fhir.cql.engine.retrieve.RetrieveSettings.TERMINOLOGY_FILTER_MODE;
import org.opencds.cqf.fhir.cql.engine.terminology.TerminologySettings.VALUESET_EXPANSION_MODE;
import org.opencds.cqf.fhir.cr.measure.MeasureEvaluationOptions;
import org.opencds.cqf.fhir.cr.measure.common.MeasurePeriodValidator;
import org.opencds.cqf.fhir.cr.measure.common.MeasureReference;
import org.opencds.cqf.fhir.cr.measure.r4.R4MultiMeasureService;
import org.opencds.cqf.fhir.utility.repository.InMemoryFhirRepository;

/**
 * Spike harness: runs an eCQM measure bundle (Measure + Libraries + ValueSets + test cases)
 * through the HL7/CQF clinical-reasoning engine.
 *
 * Usage: Harness <measure-bundle.json> tests
 *        Harness <measure-bundle.json> scale <copiesOfEachTestPatient>
 */
public class Harness {
    static final FhirContext CTX = FhirContext.forR4Cached();

    public static void main(String[] args) throws Exception {
        IParser json = CTX.newJsonParser();
        Bundle source = json.parseResource(Bundle.class, Files.readString(Path.of(args[0])));
        String mode = args[1];

        List<MeasureReport> expected = new ArrayList<>();
        Bundle content = new Bundle();
        List<Resource> data = new ArrayList<>();
        String measureId = null;
        for (var e : source.getEntry()) {
            Resource r = e.getResource();
            switch (r.getResourceType()) {
                case MeasureReport -> expected.add((MeasureReport) r);
                case Measure, Library, ValueSet -> {
                    content.addEntry().setResource(r);
                    if (r instanceof Measure) measureId = r.getIdElement().getIdPart();
                }
                default -> data.add(r);
            }
        }

        var options = MeasureEvaluationOptions.defaultOptions();
        options.getEvaluationSettings().getRetrieveSettings()
                .setSearchParameterMode(SEARCH_FILTER_MODE.FILTER_IN_MEMORY)
                .setTerminologyParameterMode(TERMINOLOGY_FILTER_MODE.FILTER_IN_MEMORY);
        options.getEvaluationSettings().getTerminologySettings()
                .setValuesetExpansionMode(VALUESET_EXPANSION_MODE.PERFORM_NAIVE_EXPANSION);

        if (mode.equals("tests")) {
            runTests(content, data, expected, measureId, options);
        } else {
            int copies = Integer.parseInt(args[2]);
            runScale(content, data, measureId, options, copies, json, args.length > 3 ? Integer.parseInt(args[3]) : 1);
        }
    }

    /** Measurement Period at millisecond precision, as MADiE test cases assume. */
    static Parameters mp(int year) {
        Parameters p = new Parameters();
        Period period = new Period();
        period.setStartElement(new DateTimeType(year + "-01-01T00:00:00.000Z"));
        period.setEndElement(new DateTimeType(year + "-12-31T23:59:59.999Z"));
        p.addParameter().setName("Measurement Period").setValue(period);
        return p;
    }

    static String subjectOf(MeasureReport mr) {
        for (var c : mr.getContained())
            if (c instanceof Parameters p)
                for (var pp : p.getParameter())
                    if (pp.getName().equals("subject")) return pp.getValue().primitiveValue();
        return null;
    }

    static void runTests(Bundle content, List<Resource> data, List<MeasureReport> expected,
                         String measureId, MeasureEvaluationOptions options) {
        Bundle all = content.copy();
        data.forEach(r -> all.addEntry().setResource(r));
        var repo = new InMemoryFhirRepository(CTX, all);
        var service = new R4MultiMeasureService(repo, options, "http://localhost", new MeasurePeriodValidator());

        int pass = 0, fail = 0;
        long t0 = System.nanoTime();
        for (MeasureReport exp : expected) {
            String subject = subjectOf(exp);
            var start = LocalDate.parse(exp.getPeriod().getStartElement().getValueAsString().substring(0, 10)).atStartOfDay(ZoneOffset.UTC);
            var end = LocalDate.parse(exp.getPeriod().getEndElement().getValueAsString().substring(0, 10)).atTime(23, 59, 59, 999_000_000).atZone(ZoneOffset.UTC);
            String desc = exp.getExtensionByUrl("http://hl7.org/fhir/us/cqfmeasures/StructureDefinition/cqfm-testCaseDescription") != null
                    ? exp.getExtensionByUrl("http://hl7.org/fhir/us/cqfmeasures/StructureDefinition/cqfm-testCaseDescription").getValue().primitiveValue() : "";
            try {
                MeasureReport got = service.evaluate(new MeasureReference.ById(new IdType("Measure", measureId)),
                        null, null, "subject", "Patient/" + subject, null, mp(start.getYear()), null, null);
                List<String> diffs = compare(exp, got);
                if (diffs.isEmpty()) pass++;
                else { fail++; System.out.println("FAIL " + subject + " [" + desc + "] " + diffs); }
            } catch (Exception ex) {
                fail++;
                System.out.println("ERROR " + subject + " [" + desc + "] " + ex); if (fail == 1) ex.printStackTrace(System.out);
            }
        }
        long ms = (System.nanoTime() - t0) / 1_000_000;
        System.out.printf("java tests: %d pass, %d fail of %d in %d ms%n", pass, fail, expected.size(), ms);
    }

    static List<String> compare(MeasureReport exp, MeasureReport got) {
        List<String> diffs = new ArrayList<>();
        for (int g = 0; g < exp.getGroup().size(); g++) {
            for (var ep : exp.getGroup().get(g).getPopulation()) {
                String code = ep.getCode().getCodingFirstRep().getCode();
                int gotCount = got.getGroup().size() > g ? got.getGroup().get(g).getPopulation().stream()
                        .filter(p -> p.getCode().getCodingFirstRep().getCode().equals(code))
                        .mapToInt(MeasureReport.MeasureReportGroupPopulationComponent::getCount).findFirst().orElse(-1) : -1;
                if (gotCount != ep.getCount()) diffs.add(code + " expected " + ep.getCount() + " got " + gotCount);
            }
        }
        return diffs;
    }

    /**
     * Clone every test patient (and the resources that mention it) N times and evaluate each synthetic
     * member against a repository holding only the measure content plus that member's data, the way a
     * CHO batch worker would read one member at a time. Library and value set caches are shared through
     * the evaluation options. Runs on a fixed thread pool.
     */
    static void runScale(Bundle content, List<Resource> data, String measureId,
                         MeasureEvaluationOptions options, int copies, IParser json, int threads) throws Exception {
        Map<String, List<String>> byPatient = new LinkedHashMap<>();
        List<String> patientIds = data.stream().filter(r -> r instanceof Patient).map(r -> r.getIdElement().getIdPart()).toList();
        for (Resource r : data) {
            String s = json.encodeResourceToString(r);
            for (String pid : patientIds)
                if (s.contains(pid)) { byPatient.computeIfAbsent(pid, k -> new ArrayList<>()).add(s); break; }
        }
        var counts = new java.util.concurrent.ConcurrentHashMap<String, java.util.concurrent.atomic.AtomicLong>();
        var pool = java.util.concurrent.Executors.newFixedThreadPool(threads);
        var members = new java.util.concurrent.atomic.AtomicLong();
        var mp = mp(2026);
        long t1 = System.nanoTime();
        List<java.util.concurrent.Future<?>> futures = new ArrayList<>();
        for (int c = 0; c < copies; c++) {
            final int copy = c;
            futures.add(pool.submit(() -> {
                IParser p = CTX.newJsonParser();
                // One repository per batch of 66 members (one copy of every test patient), population report.
                Bundle b = content.copy();
                for (var en : byPatient.entrySet()) {
                    String newPid = en.getKey().substring(0, 24) + String.format("%012d", copy);
                    for (String s : en.getValue())
                        b.addEntry().setResource((Resource) p.parseResource(s.replace(en.getKey(), newPid)));
                    members.incrementAndGet();
                }
                var repo = new InMemoryFhirRepository(CTX, b);
                var service = new R4MultiMeasureService(repo, options, "http://localhost", new MeasurePeriodValidator());
                MeasureReport report = service.evaluate(new MeasureReference.ById(new IdType("Measure", measureId)),
                        null, null, "population", null, null, mp, null, null);
                for (var pop : report.getGroupFirstRep().getPopulation())
                    counts.computeIfAbsent(pop.getCode().getCodingFirstRep().getCode(), k -> new java.util.concurrent.atomic.AtomicLong()).addAndGet(pop.getCount());
                return null;
            }));
        }
        for (var f : futures) f.get();
        pool.shutdown();
        long evalMs = (System.nanoTime() - t1) / 1_000_000;
        Runtime rt = Runtime.getRuntime();
        long usedMb = (rt.totalMemory() - rt.freeMemory()) / (1024 * 1024);
        System.out.printf("java scale: %d members, threads %d, evaluate %d ms (%.2f ms/member), heap used %d MB, %s%n",
                members.get(), threads, evalMs, (double) evalMs / members.get(), usedMb, new TreeMap<>(counts));
    }
}
