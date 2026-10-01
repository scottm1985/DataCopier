using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using MyscotekDataCopier.Core.Services;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    public class CellFormatterTests
    {
        private static readonly Guid Id = new Guid("a0000000-0000-0000-0000-00000000000a");

        [Fact]
        public void Formatted_value_wins_over_the_raw_value()
        {
            var entity = new Entity("account") { ["industrycode"] = new OptionSetValue(3) };
            entity.FormattedValues["industrycode"] = "Retail";

            Assert.Equal("Retail", CellFormatter.Format(entity, "industrycode"));
        }

        [Fact]
        public void Missing_column_null_value_and_null_entity_give_an_empty_string()
        {
            var entity = new Entity("account") { ["description"] = null };

            Assert.Equal(string.Empty, CellFormatter.Format(entity, "name"));
            Assert.Equal(string.Empty, CellFormatter.Format(entity, "description"));
            Assert.Equal(string.Empty, CellFormatter.Format(null, "name"));
        }

        [Fact]
        public void EntityReference_shows_its_name_or_else_its_id()
        {
            var entity = new Entity("contact")
            {
                ["parentcustomerid"] = new EntityReference("account", Id) { Name = "Contoso" },
                ["ownerid"] = new EntityReference("systemuser", Id)
            };

            Assert.Equal("Contoso", CellFormatter.Format(entity, "parentcustomerid"));
            Assert.Equal(Id.ToString(), CellFormatter.Format(entity, "ownerid"));
        }

        [Fact]
        public void AliasedValue_is_unwrapped_and_formatted_by_its_inner_type()
        {
            var entity = new Entity("account")
            {
                ["c.fullname"] = new AliasedValue("contact", "fullname", "Jane Doe"),
                ["c.parentcustomerid"] = new AliasedValue("contact", "parentcustomerid", new EntityReference("account", Id) { Name = "Contoso" })
            };

            Assert.Equal("Jane Doe", CellFormatter.Format(entity, "c.fullname"));
            Assert.Equal("Contoso", CellFormatter.Format(entity, "c.parentcustomerid"));
        }

        [Fact]
        public void AliasedValue_option_set_and_money_are_formatted_and_formatted_values_keyed_by_alias_win()
        {
            WithCulture(CultureInfo.InvariantCulture, () =>
            {
                var entity = new Entity("account")
                {
                    ["c.gendercode"] = new AliasedValue("contact", "gendercode", new OptionSetValue(2)),
                    ["c.creditlimit"] = new AliasedValue("contact", "creditlimit", new Money(10m)),
                    ["c.familystatuscode"] = new AliasedValue("contact", "familystatuscode", new OptionSetValue(1))
                };
                entity.FormattedValues["c.familystatuscode"] = "Single";

                Assert.Equal("2", CellFormatter.Format(entity, "c.gendercode"));
                Assert.Equal("10.00", CellFormatter.Format(entity, "c.creditlimit"));
                Assert.Equal("Single", CellFormatter.Format(entity, "c.familystatuscode"));
            });
        }

        [Fact]
        public void Money_option_sets_bool_and_images_are_formatted_by_type()
        {
            WithCulture(CultureInfo.InvariantCulture, () =>
            {
                var options = new OptionSetValueCollection { new OptionSetValue(1), new OptionSetValue(3) };
                var entity = new Entity("account")
                {
                    ["revenue"] = new Money(1234.5m),
                    ["industrycode"] = new OptionSetValue(7),
                    ["new_tags"] = options,
                    ["creditonhold"] = true,
                    ["entityimage"] = new byte[] { 1, 2, 3 },
                    ["numberofemployees"] = 42
                };

                Assert.Equal("1,234.50", CellFormatter.Format(entity, "revenue"));
                Assert.Equal("7", CellFormatter.Format(entity, "industrycode"));
                Assert.Equal("1, 3", CellFormatter.Format(entity, "new_tags"));
                Assert.Equal("True", CellFormatter.Format(entity, "creditonhold"));
                Assert.Equal("(image)", CellFormatter.Format(entity, "entityimage"));
                Assert.Equal("42", CellFormatter.Format(entity, "numberofemployees"));
            });
        }

        [Fact]
        public void DateTime_is_shown_in_local_time_with_the_general_short_format()
        {
            WithCulture(new CultureInfo("en-GB"), () =>
            {
                var utc = new DateTime(2024, 3, 1, 14, 30, 0, DateTimeKind.Utc);
                var entity = new Entity("account") { ["createdon"] = utc };

                Assert.Equal(utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), CellFormatter.Format(entity, "createdon"));
                Assert.Matches(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}$", CellFormatter.Format(entity, "createdon"));
            });
        }

        private static void WithCulture(CultureInfo culture, Action action)
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = culture;
            try
            {
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }
}
