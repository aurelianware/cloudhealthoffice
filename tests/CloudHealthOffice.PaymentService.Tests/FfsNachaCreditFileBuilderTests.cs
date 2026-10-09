using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// The FFS NACHA CCD+ file layout: 94-character records, blocking factor 10,
/// record order, one addenda (type 05) per entry carrying the 835 TRN, batch
/// and file control counts, entry hash and totals, and byte-for-byte
/// reproducibility.
/// </summary>
public class FfsNachaCreditFileBuilderTests
{
    internal static FfsNachaFileHeader Header() => new()
    {
        ImmediateDestination = "091000019",
        ImmediateOrigin = "1123456789",
        ImmediateDestinationName = "FIRST BANK",
        ImmediateOriginName = "CLOUD HEALTH OFFICE",
        CompanyName = "CLOUD HEALTH OFC",
        CompanyId = "1123456789",
        OriginatingDfi = "09100001",
        ReferenceCode = "A1B2C3",
        FileIdModifier = "A",
        FileCreatedAt = new DateTime(2026, 5, 1, 14, 30, 0, DateTimeKind.Utc),
        EffectiveEntryDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
    };

    internal static FfsNachaCreditEntry Entry(string routing, string account, decimal amount, string trace, bool savings = false, string npi = "1234567890") => new()
    {
        RoutingNumber = routing,
        AccountNumber = account,
        IsSavings = savings,
        Amount = amount,
        IdentificationNumber = npi,
        ReceiverName = "Sunrise Clinic",
        ReassociationTrace = trace,
    };

    private static string[] Records(FfsNachaBuiltFile file)
    {
        Assert.EndsWith("\n", file.Content);
        return file.Content.TrimEnd('\n').Split('\n');
    }

    [Fact]
    public void Every_record_is_94_characters_and_the_file_is_blocked_by_10()
    {
        var file = FfsNachaCreditFileBuilder.Build(Header(), new[]
        {
            Entry("021000021", "111122223333", 1250.75m, "0001000000"),
            Entry("026009593", "444455556666", 99.25m, "0001000001", savings: true),
            Entry("121000358", "777788889999", 0.01m, "0001000002"),
        });

        var records = Records(file);
        Assert.All(records, r => Assert.Equal(94, r.Length));
        Assert.Equal(0, records.Length % 10);
        // 1 + 5 + 3 x (6 + 7) + 8 + 9 = 10 records: exactly one block, no filler.
        Assert.Equal(10, records.Length);
        Assert.Equal(1, file.BlockCount);
        Assert.Equal("1567676789", string.Concat(records.Select(r => r[0])));

        var header = records[0];
        Assert.Equal("101", header[..3]);
        Assert.Equal(" 091000019", header[3..13]);
        Assert.Equal("1123456789", header[13..23]);
        Assert.Equal("2605011430", header[23..33]);
        Assert.Equal("A09410", header[33..39]);
        Assert.Equal("1", header[39..40]);
    }

    [Fact]
    public void Filler_records_of_nines_pad_the_last_block()
    {
        var file = FfsNachaCreditFileBuilder.Build(Header(), new[] { Entry("021000021", "111122223333", 10m, "T1") });

        var records = Records(file);
        // 1 + 5 + 6 + 7 + 8 + 9 = 6 records, padded with four '9' records.
        Assert.Equal(10, records.Length);
        Assert.All(records[6..], r => Assert.Equal(new string('9', 94), r));
        Assert.Equal('9', records[5][0]);
        Assert.Equal("000001", records[5][7..13]); // block count
    }

    [Fact]
    public void Batch_header_is_ccd_credits_only_hcclaimpmt_with_the_runs_dates()
    {
        var file = FfsNachaCreditFileBuilder.Build(Header(), new[] { Entry("021000021", "111122223333", 10m, "T1") });
        var batch = Records(file)[1];

        Assert.Equal("5220", batch[..4]);
        Assert.Equal("CLOUD HEALTH OFC", batch[4..20]);
        Assert.Equal("1123456789", batch[40..50]);
        Assert.Equal("CCD", batch[50..53]);
        Assert.Equal("HCCLAIMPMT", batch[53..63]);
        Assert.Equal("260501", batch[63..69]);
        Assert.Equal("260504", batch[69..75]);
        Assert.Equal("1", batch[78..79]);
        Assert.Equal("09100001", batch[79..87]);
        Assert.Equal("0000001", batch[87..94]);
    }

    [Fact]
    public void Entry_detail_and_addenda_carry_the_credit_and_the_835_trn()
    {
        var file = FfsNachaCreditFileBuilder.Build(Header(), new[]
        {
            Entry("021000021", "111122223333", 1250.75m, "0001000000"),
            Entry("026009593", "444455556666", 99.25m, "0001000001", savings: true),
        });
        var records = Records(file);

        var entry = records[2];
        Assert.Equal("622", entry[..3]);                       // checking credit
        Assert.Equal("02100002", entry[3..11]);
        Assert.Equal("1", entry[11..12]);                       // check digit
        Assert.Equal("111122223333     ", entry[12..29]);
        Assert.Equal("0000125075", entry[29..39]);
        Assert.Equal("1234567890     ", entry[39..54]);
        Assert.Equal("SUNRISE CLINIC        ", entry[54..76]);
        Assert.Equal("1", entry[78..79]);                       // addenda follows
        Assert.Equal("091000010000001", entry[79..94]);

        var addenda = records[3];
        Assert.Equal("705", addenda[..3]);
        Assert.Equal("TRN*1*0001000000*1123456789\\", addenda[3..83].TrimEnd());
        Assert.Equal("0001", addenda[83..87]);
        Assert.Equal("0000001", addenda[87..94]);              // = entry trace sequence

        Assert.Equal("632", records[4][..3]);                   // savings credit
        Assert.Equal("TRN*1*0001000001*1123456789\\", records[5][3..83].TrimEnd());
        Assert.Equal("0000002", records[5][87..94]);
        Assert.Equal(new[] { "091000010000001", "091000010000002" }, file.AchTraceNumbers);
    }

    [Fact]
    public void Batch_and_file_control_totals_counts_and_entry_hash()
    {
        var file = FfsNachaCreditFileBuilder.Build(Header(), new[]
        {
            Entry("021000021", "111122223333", 1250.75m, "T1"),
            Entry("026009593", "444455556666", 99.25m, "T2"),
            Entry("121000358", "777788889999", 0.01m, "T3"),
        });
        var records = Records(file);

        // Hash: 02100002 + 02600959 + 12100035 = 16800996
        const string hash = "0016800996";
        Assert.Equal(hash, file.EntryHash);
        Assert.Equal(1350.01m, file.TotalCreditAmount);

        var batchControl = records[8];
        Assert.Equal("8220", batchControl[..4]);
        Assert.Equal("000006", batchControl[4..10]);           // 3 entries + 3 addenda
        Assert.Equal(hash, batchControl[10..20]);
        Assert.Equal("000000000000", batchControl[20..32]);    // debits
        Assert.Equal("000000135001", batchControl[32..44]);    // credits, cents
        Assert.Equal("1123456789", batchControl[44..54]);
        Assert.Equal("09100001", batchControl[79..87]);
        Assert.Equal("0000001", batchControl[87..94]);

        var fileControl = records[9];
        Assert.Equal("9000001000001", fileControl[..13]);       // 1 batch, 1 block
        Assert.Equal("00000006", fileControl[13..21]);
        Assert.Equal(hash, fileControl[21..31]);
        Assert.Equal("000000000000", fileControl[31..43]);
        Assert.Equal("000000135001", fileControl[43..55]);
        Assert.Equal(new string(' ', 39), fileControl[55..94]);

        // What the shared transmission library reads back from the file agrees.
        var facts = CloudHealthOffice.NachaTransmission.NachaFileFacts.From(file.Content);
        Assert.Equal((3, 0m, 1350.01m), (facts.EntryCount, facts.TotalDebitAmount, facts.TotalCreditAmount));
        Assert.Equal(file.Sha256, facts.Sha256);
    }

    [Fact]
    public void Entry_hash_keeps_the_rightmost_ten_digits()
    {
        // 1300 entries of routing 99999999x: the sum overflows ten digits.
        var entries = Enumerable.Range(0, 1300)
            .Select(i => Entry("999999992", "ACCT" + i, 1m, "T" + i))
            .ToList();
        var file = FfsNachaCreditFileBuilder.Build(Header(), entries);

        var expected = (99999999L * 1300 % 10_000_000_000L).ToString("0000000000");
        Assert.Equal(expected, file.EntryHash);
        var records = Records(file);
        Assert.Equal(expected, records.Single(r => r[0] == '8')[10..20]);
        // 2 + 2600 + 2 = 2604 records -> 261 blocks.
        Assert.Equal(261, file.BlockCount);
        Assert.Equal(2610, records.Length);
    }

    [Fact]
    public void The_same_input_builds_the_same_bytes()
    {
        var entries = new[] { Entry("021000021", "111122223333", 1250.75m, "T1"), Entry("026009593", "444455556666", 99.25m, "T2") };

        var first = FfsNachaCreditFileBuilder.Build(Header(), entries);
        var second = FfsNachaCreditFileBuilder.Build(Header(), entries);

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Theory]
    [InlineData("02100002", "111", "10")]      // 8-digit routing
    [InlineData("021000021", "", "10")]        // no account
    [InlineData("021000021", "111", "0")]      // zero credit
    [InlineData("021000021", "111", "10.005")] // fractional cents
    public void Invalid_entries_are_refused(string routing, string account, string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<InvalidOperationException>(() =>
            FfsNachaCreditFileBuilder.Build(Header(), new[] { Entry(routing, account, value, "T1") }));
    }

    private static FfsNachaFileHeader HeaderWithCompanyId(string companyId)
    {
        var h = Header();
        return new FfsNachaFileHeader
        {
            ImmediateDestination = h.ImmediateDestination, ImmediateOrigin = h.ImmediateOrigin,
            CompanyName = h.CompanyName, CompanyId = companyId, OriginatingDfi = h.OriginatingDfi,
            FileCreatedAt = h.FileCreatedAt, EffectiveEntryDate = h.EffectiveEntryDate,
        };
    }

    [Fact]
    public void A_lower_case_company_id_is_refused_so_batch_and_addenda_trn03_cannot_differ()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FfsNachaCreditFileBuilder.Build(HeaderWithCompanyId("1abcdefghi"), new[] { Entry("021000021", "111", 1m, "T1") }));
        Assert.Contains("upper-case", ex.Message);
    }

    [Fact]
    public void An_alphanumeric_company_id_is_written_identically_in_the_batch_and_the_addenda()
    {
        var file = FfsNachaCreditFileBuilder.Build(HeaderWithCompanyId("1ABC123456"), new[] { Entry("021000021", "111", 1m, "T1") });
        var records = file.Content.TrimEnd('\n').Split('\n');

        Assert.Equal("1ABC123456", records[1][40..50]);
        Assert.Equal("1ABC123456", records[4][44..54]);
        Assert.Equal("TRN*1*T1*1ABC123456\\", records[3][3..83].TrimEnd());
    }

    [Theory]
    [InlineData("021000021", true)]
    [InlineData("026009593", true)]
    [InlineData("091000019", true)]
    [InlineData("021000022", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678", false)]
    public void The_aba_check_digit_is_validated(string routing, bool valid)
    {
        Assert.Equal(valid, FfsNachaCreditFileBuilder.IsValidAbaRoutingNumber(routing));
    }

    [Fact]
    public void An_entry_whose_routing_number_fails_the_aba_check_digit_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FfsNachaCreditFileBuilder.Build(Header(), new[] { Entry("021000022", "111", 1m, "T1") }));
        Assert.Contains("ABA", ex.Message);
    }

    [Fact]
    public void More_credits_than_the_six_digit_entry_addenda_count_allows_are_refused_up_front()
    {
        var entry = Entry("021000021", "111", 1m, "T1");
        var entries = Enumerable.Repeat(entry, FfsNachaCreditFileBuilder.MaxEntries + 1).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => FfsNachaCreditFileBuilder.Build(Header(), entries));
        Assert.Contains("499,999", ex.Message);
    }

    [Fact]
    public void A_total_that_does_not_fit_twelve_digits_is_refused_up_front()
    {
        var entries = Enumerable.Range(0, 101)
            .Select(i => Entry("021000021", "A" + i, FfsNachaCreditFileBuilder.MaxEntryAmount, "T" + i))
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => FfsNachaCreditFileBuilder.Build(Header(), entries));
        Assert.Contains("12-digit", ex.Message);
    }

    [Fact]
    public void A_trace_with_x12_delimiters_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FfsNachaCreditFileBuilder.Build(Header(), new[] { Entry("021000021", "111", 1m, "T*1") }));
    }

    [Fact]
    public void A_company_id_that_is_not_ten_characters_is_refused()
    {
        var header = Header();
        var bad = new FfsNachaFileHeader
        {
            ImmediateDestination = header.ImmediateDestination, ImmediateOrigin = header.ImmediateOrigin,
            CompanyName = header.CompanyName, CompanyId = "123", OriginatingDfi = header.OriginatingDfi,
            FileCreatedAt = header.FileCreatedAt, EffectiveEntryDate = header.EffectiveEntryDate,
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FfsNachaCreditFileBuilder.Build(bad, new[] { Entry("021000021", "111", 1m, "T1") }));
        Assert.Contains("TRN03", ex.Message);
    }
}
