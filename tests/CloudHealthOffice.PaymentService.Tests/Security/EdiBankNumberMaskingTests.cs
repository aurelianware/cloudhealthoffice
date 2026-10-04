using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

public class EdiBankNumberMaskingTests
{
    [Fact]
    public void X12Layout_MasksBothDfiAndAccountNumbers_KeepsOtherElements()
    {
        // BPR06-09 payer DFI/account, BPR10 originating company, BPR11 supplemental,
        // BPR12-15 payee DFI/account, BPR16 date.
        var edi = "ST*835*0001~BPR*C*100.00*C*ACH*CCP*01*011000015*DA*123456789012*1123456789**01*021000021*DA*987654321098*20260504~SE*2*0001~";

        var masked = EdiBankNumberMasking.MaskBpr(edi);

        Assert.Equal(
            "ST*835*0001~BPR*C*100.00*C*ACH*CCP*01*XXXXX0015*DA*XXXXXXXX9012*1123456789**01*XXXXX0021*DA*XXXXXXXX1098*20260504~SE*2*0001~",
            masked);
    }

    [Fact]
    public void CheckPayment_HasNoBankNumbers_IsUnchanged()
    {
        var edi = "ST*835*0001~BPR*C*100.00*C*CHK****20260504~TRN*1*0001000000*1999999999~";

        Assert.Same(edi, EdiBankNumberMasking.MaskBpr(edi));
    }

    [Fact]
    public void OnlyBprIsTouched()
    {
        var edi = "N1*PE*CLINIC*XX*1234567893~REF*DA*123456789012~BPR*I*0.00*C*NON****20260504~";

        Assert.Equal(edi, EdiBankNumberMasking.MaskBpr(edi));
    }
}
