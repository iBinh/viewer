using OpenIPC.Viewer.Core.Onvif;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Onvif;

// The sweep POSTs the probe envelope at every host with an HTTP port open, so
// this predicate decides whether a printer, a router or a NAS gets labelled a
// camera. Pure, so it tests without a socket.
public sealed class OnvifProbeSignatureTests
{
    // Trimmed shape of what a real camera answered on 192.168.3.137.
    private const string SuccessBody =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\" " +
        "xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">" +
        "<SOAP-ENV:Body><tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime>" +
        "<tt:UTCDateTime xmlns:tt=\"http://www.onvif.org/ver10/schema\" />" +
        "</tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse></SOAP-ENV:Body></SOAP-ENV:Envelope>";

    [Fact]
    public void SuccessfulGetSystemDateAndTime_IsOnvif()
    {
        Assert.True(OnvifProbeSignature.LooksLikeOnvif(200, SuccessBody));
    }

    [Fact]
    public void SoapFault_IsStillOnvif_BecauseOnlyAnOnvifStackAnswersThatNamespace()
    {
        // Some firmwares fault this call (wrong action, auth required) but the
        // fault still carries the onvif.org namespace.
        const string fault =
            "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\">" +
            "<SOAP-ENV:Body><SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value>" +
            "<SOAP-ENV:Subcode><SOAP-ENV:Value xmlns:ter=\"http://www.onvif.org/ver10/error\">" +
            "ter:NotAuthorized</SOAP-ENV:Value></SOAP-ENV:Subcode></SOAP-ENV:Code>" +
            "</SOAP-ENV:Fault></SOAP-ENV:Body></SOAP-ENV:Envelope>";

        Assert.True(OnvifProbeSignature.LooksLikeOnvif(500, fault));
    }

    [Fact]
    public void Unauthorized_IsOnvif_WithoutLookingAtABody()
    {
        // A firmware that guards the whole endpoint challenges here; an
        // unrelated web server 404s a path it doesn't serve instead.
        Assert.True(OnvifProbeSignature.LooksLikeOnvif(401, null));
        Assert.True(OnvifProbeSignature.LooksLikeOnvif(401, ""));
    }

    [Theory]
    [InlineData(404, "<html><head><title>404 Not Found</title></head><body>nginx</body></html>")]
    [InlineData(200, "<html><body><h1>Router admin</h1><form action=\"/login\"></form></body></html>")]
    [InlineData(405, "Method Not Allowed")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(200, "{\"system\":{},\"video0\":{}}")]  // a Majestic API, not an ONVIF one
    public void EverythingElseOnPort80_IsNotOnvif(int statusCode, string body)
    {
        Assert.False(OnvifProbeSignature.LooksLikeOnvif(statusCode, body));
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(200, "")]
    [InlineData(204, null)]
    public void EmptyBodyWithoutAChallenge_IsNotOnvif(int statusCode, string? body)
    {
        // A bare 200 proves only that something is listening.
        Assert.False(OnvifProbeSignature.LooksLikeOnvif(statusCode, body));
    }

    [Fact]
    public void NamespaceMatchIsCaseInsensitive()
    {
        Assert.True(OnvifProbeSignature.LooksLikeOnvif(200, "<x xmlns=\"HTTP://WWW.ONVIF.ORG/ver10/schema\"/>"));
    }

    [Fact]
    public void RequestEnvelope_AsksForTheUnauthenticatedCall()
    {
        // If this ever stops being GetSystemDateAndTime the probe starts needing
        // credentials, and the sweep has none.
        Assert.Contains("GetSystemDateAndTime", OnvifProbeSignature.RequestEnvelope);
        Assert.Contains("http://www.onvif.org/ver10/device/wsdl", OnvifProbeSignature.RequestEnvelope);
        Assert.StartsWith("<?xml", OnvifProbeSignature.RequestEnvelope);
    }
}
