using System.Text;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core.Network
{
    // WHATWG MIME Sniffing 7.1: only a missing supplied type or unknown/unknown,
    // application/unknown and */* run the unknown-type algorithm, and that
    // algorithm never promotes text to JSON or script.
    public class MimeSnifferTests
    {
        [Fact]
        public void Sniff_Detects_Html_From_Doctype()
        {
            var data = Encoding.UTF8.GetBytes("<!DOCTYPE html><html>...</html>");
            Assert.Equal("text/html", MimeSniffer.SniffMimeType(data, null));
            Assert.Equal("text/html", MimeSniffer.SniffMimeType(data, "unknown/unknown"));
        }

        // application/octet-stream is a real supplied type, so HTML bytes served
        // with it are not promoted to scriptable HTML.
        [Fact]
        public void Sniff_Keeps_Declared_OctetStream()
        {
            var data = Encoding.UTF8.GetBytes("<!DOCTYPE html><html>...</html>");
            Assert.Equal("application/octet-stream", MimeSniffer.SniffMimeType(data, "application/octet-stream"));
        }

        [Fact]
        public void Sniff_Does_Not_Promote_Json()
        {
            var data = Encoding.UTF8.GetBytes("{ \"key\": \"value\" }");
            Assert.Equal("text/plain", MimeSniffer.SniffMimeType(data, null));
        }

        [Fact]
        public void Sniff_Respects_Declared_Text_Type()
        {
            var data = Encoding.UTF8.GetBytes("Just some text");
            var mime = MimeSniffer.SniffMimeType(data, "text/plain");
            Assert.Equal("text/plain", mime);
        }

        [Fact]
        public void Sniff_Keeps_Declared_Text_Type_For_Binary_Bytes()
        {
            var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF };
            Assert.Equal("text/plain", MimeSniffer.SniffMimeType(data, "text/plain"));
        }

        [Fact]
        public void Sniff_Detects_Binary_For_Unknown_Type()
        {
            var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF };
            Assert.Equal("application/octet-stream", MimeSniffer.SniffMimeType(data, null));
        }

        [Fact]
        public void Sniff_Detects_Images()
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.Equal("image/png", MimeSniffer.SniffMimeType(png, null));

            var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
            Assert.Equal("image/gif", MimeSniffer.SniffMimeType(gif, null));
        }

        [Fact]
        public void Sniff_Detects_AudioVideo_Extensions()
        {
            // MIME Sniffing 6.2 maps the OggS signature to application/ogg.
            var ogg = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00 };
            Assert.Equal("application/ogg", MimeSniffer.SniffMimeType(ogg, null));

            var webm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 };
            Assert.Equal("video/webm", MimeSniffer.SniffMimeType(webm, null));

            // MP3 (ID3)
            var mp3 = new byte[] { 0x49, 0x44, 0x33, 0x03 };
            Assert.Equal("audio/mpeg", MimeSniffer.SniffMimeType(mp3, null));
        }

        [Fact]
        public void Sniff_Does_Not_Promote_JavaScript()
        {
            var data = Encoding.UTF8.GetBytes("  /* prelude */\nwindow.WIMB.detect();");
            Assert.Equal("text/plain", MimeSniffer.SniffMimeType(data, null));
        }
    }
}
