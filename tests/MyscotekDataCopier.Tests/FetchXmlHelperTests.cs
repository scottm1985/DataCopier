using System;
using System.Linq;
using System.Xml.Linq;
using MyscotekDataCopier.Core.Services;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    public class FetchXmlHelperTests
    {
        private const string Fetch =
            "<fetch version=\"1.0\" top=\"50\" mapping=\"logical\">" +
            "<entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>";

        private const string Cookie = "<cookie page=\"2\"><accountid last=\"{8F1C0A2B-0000-0000-0000-000000000001}\" first=\"{8F1C0A2B-0000-0000-0000-000000000002}\" /></cookie>";

        [Fact]
        public void ApplyPaging_sets_count_page_and_cookie_and_removes_top()
        {
            string result = FetchXmlHelper.ApplyPaging(Fetch, 3, 250, Cookie);

            XElement fetch = XElement.Parse(result);
            Assert.Null(fetch.Attribute("top"));
            Assert.Equal("250", (string)fetch.Attribute("count"));
            Assert.Equal("3", (string)fetch.Attribute("page"));
            Assert.Equal(Cookie, (string)fetch.Attribute("paging-cookie"));
            Assert.Equal("1.0", (string)fetch.Attribute("version"));
            Assert.Equal("account", (string)fetch.Element("entity").Attribute("name"));
        }

        [Fact]
        public void ApplyPaging_keeps_the_other_fetch_attributes()
        {
            XElement fetch = XElement.Parse(FetchXmlHelper.ApplyPaging(
                "<fetch distinct=\"true\" no-lock=\"true\" count=\"5\" page=\"9\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>", 1, 50, null));

            Assert.Equal(("true", "true", "50", "1"),
                ((string)fetch.Attribute("distinct"), (string)fetch.Attribute("no-lock"), (string)fetch.Attribute("count"), (string)fetch.Attribute("page")));
        }

        [Fact]
        public void ApplyPaging_xml_escapes_the_paging_cookie()
        {
            string result = FetchXmlHelper.ApplyPaging(Fetch, 2, 50, Cookie);

            Assert.Contains("paging-cookie=\"&lt;cookie page=&quot;2&quot;", result);
            Assert.DoesNotContain("<cookie", result);
        }

        [Fact]
        public void ApplyPaging_replaces_existing_paging_and_removes_the_cookie_when_none_is_given()
        {
            string paged = FetchXmlHelper.ApplyPaging(Fetch, 2, 50, Cookie);

            XElement fetch = XElement.Parse(FetchXmlHelper.ApplyPaging(paged, 1, 10, null));

            Assert.Equal("10", (string)fetch.Attribute("count"));
            Assert.Equal("1", (string)fetch.Attribute("page"));
            Assert.Null(fetch.Attribute("paging-cookie"));
        }

        [Theory]
        [InlineData(0, 10)]
        [InlineData(1, 0)]
        public void ApplyPaging_rejects_an_invalid_page_or_count(int page, int count)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FetchXmlHelper.ApplyPaging(Fetch, page, count, null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("<fetch><entity>")]
        [InlineData("<grid />")]
        public void ApplyPaging_rejects_invalid_fetch_xml(string fetchXml)
        {
            Assert.Throws<ArgumentException>(() => FetchXmlHelper.ApplyPaging(fetchXml, 1, 10, null));
        }

        [Fact]
        public void EnsureAttribute_adds_the_attribute_to_the_main_entity_after_the_existing_attributes()
        {
            const string fetchXml =
                "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" />" +
                "<link-entity name=\"contact\" from=\"contactid\" to=\"primarycontactid\" alias=\"c\"><attribute name=\"accountid\" /></link-entity>" +
                "</entity></fetch>";

            XElement entity = XElement.Parse(FetchXmlHelper.EnsureAttribute(fetchXml, "accountid")).Element("entity");

            Assert.Equal(new[] { "name", "accountid" }, entity.Elements("attribute").Select(a => (string)a.Attribute("name")));
            Assert.Equal("attribute", entity.Elements().ElementAt(1).Name.LocalName);   // inserted right after the last attribute
        }

        [Fact]
        public void EnsureAttribute_adds_the_attribute_first_when_the_entity_lists_none()
        {
            const string fetchXml = "<fetch><entity name=\"account\"><order attribute=\"name\" /></entity></fetch>";

            XElement entity = XElement.Parse(FetchXmlHelper.EnsureAttribute(fetchXml, "accountid")).Element("entity");

            Assert.Equal("accountid", (string)entity.Elements().First().Attribute("name"));
        }

        [Theory]
        [InlineData("<fetch><entity name=\"account\"><attribute name=\"accountid\" /></entity></fetch>")]
        [InlineData("<fetch><entity name=\"account\"><attribute name=\"AccountId\" /></entity></fetch>")]
        [InlineData("<fetch><entity name=\"account\"><all-attributes /></entity></fetch>")]
        public void EnsureAttribute_returns_the_fetch_unchanged_when_the_attribute_is_already_returned(string fetchXml)
        {
            Assert.Equal(fetchXml, FetchXmlHelper.EnsureAttribute(fetchXml, "accountid"));
        }
    }
}
