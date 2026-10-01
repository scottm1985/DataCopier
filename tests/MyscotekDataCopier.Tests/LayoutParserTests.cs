using System.Linq;
using MyscotekDataCopier.Core.Services;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    public class LayoutParserTests
    {
        [Fact]
        public void Parses_cell_names_and_widths_in_document_order()
        {
            const string layout =
                "<grid name=\"resultset\" object=\"1\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\">" +
                "<row name=\"result\" id=\"accountid\">" +
                "<cell name=\"name\" width=\"300\" />" +
                "<cell name=\"a_1.fullname\" width=\"150\" />" +
                "<cell name=\"\" width=\"100\" />" +
                "<cell width=\"80\" />" +
                "<cell name=\"createdon\" width=\"125\" />" +
                "</row></grid>";

            var columns = LayoutParser.Parse(layout);

            Assert.Equal(new[] { ("name", 300), ("a_1.fullname", 150), ("createdon", 125) },
                columns.Select(c => (c.Name, c.Width)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not xml at all")]
        [InlineData("<grid><row><cell name=\"name\" width=\"100\">")]
        public void Missing_or_invalid_xml_gives_an_empty_list(string layout)
        {
            Assert.Empty(LayoutParser.Parse(layout));
        }

        [Fact]
        public void Missing_or_invalid_width_falls_back_to_the_default()
        {
            var columns = LayoutParser.Parse("<grid><row><cell name=\"a\" /><cell name=\"b\" width=\"abc\" /><cell name=\"c\" width=\"0\" /><cell name=\"d\" width=\"90px\" /></row></grid>");

            Assert.Equal(new[] { LayoutParser.DefaultWidth, LayoutParser.DefaultWidth, LayoutParser.DefaultWidth, 90 }, columns.Select(c => c.Width));
        }

        [Fact]
        public void Repeated_cell_names_are_returned_once()
        {
            var columns = LayoutParser.Parse("<grid><row><cell name=\"name\" width=\"100\" /><cell name=\"NAME\" width=\"200\" /></row></grid>");

            Assert.Equal(100, Assert.Single(columns).Width);
        }
    }
}
