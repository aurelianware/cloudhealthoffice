using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ServiceUnavailableExceptionTests
{
    [Fact]
    public void Constructor_SetsServiceName()
    {
        var ex = new ServiceUnavailableException("Claims Service");
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public void Constructor_MessageContainsServiceName()
    {
        var ex = new ServiceUnavailableException("Payment Service");
        ex.Message.Should().Contain("Payment Service");
    }

    [Fact]
    public void Constructor_MessageContainsUserFriendlyText()
    {
        var ex = new ServiceUnavailableException("Eligibility Service");
        ex.Message.Should().Contain("Please try again");
        ex.Message.Should().Contain("contact your administrator");
    }

    [Fact]
    public void Constructor_PreservesInnerException()
    {
        var inner = new HttpRequestException("Connection refused");
        var ex = new ServiceUnavailableException("Claims Service", inner);

        ex.InnerException.Should().BeSameAs(inner);
        ex.InnerException.Should().BeOfType<HttpRequestException>();
        ex.InnerException!.Message.Should().Be("Connection refused");
    }

    [Fact]
    public void Constructor_InnerExceptionIsNullByDefault()
    {
        var ex = new ServiceUnavailableException("Claims Service");
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void IsException_DerivedFromSystemException()
    {
        var ex = new ServiceUnavailableException("Test");
        ex.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void DifferentServiceNames_ProduceDifferentMessages()
    {
        var ex1 = new ServiceUnavailableException("Claims Service");
        var ex2 = new ServiceUnavailableException("Authorization Service");

        ex1.Message.Should().NotBe(ex2.Message);
        ex1.ServiceName.Should().NotBe(ex2.ServiceName);
    }
}
