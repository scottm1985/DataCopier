using System;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using MyscotekDataCopier.Tests.Fakes;
using MyscotekDataCopier.UI;
using XrmToolBox.Extensibility.Interfaces;
using Xunit;

namespace MyscotekDataCopier.Tests
{
    /// <summary>
    /// What XrmToolBox and its Tool Library read from the plugin: the MEF export and its seven metadata
    /// keys, the dedicated small and large images, the assembly attributes and version (which must
    /// equal the NuGet package version), and the GitHub / help links of the control.
    /// </summary>
    public class PluginMetadataTests
    {
        private static ExportMetadataAttribute[] Exports() =>
            typeof(MyscotekDataCopierPlugin).GetCustomAttributes<ExportMetadataAttribute>().ToArray();

        private static string Metadata(string key) => (string)Exports().Single(a => a.Name == key).Value;

        [Fact]
        public void The_export_has_all_seven_metadata_keys_and_the_Data_Copier_name()
        {
            Assert.Equal(typeof(IXrmToolBoxPlugin), typeof(MyscotekDataCopierPlugin).GetCustomAttribute<ExportAttribute>().ContractType);
            Assert.Equal(new[] { "BackgroundColor", "BigImageBase64", "Description", "Name", "PrimaryFontColor", "SecondaryFontColor", "SmallImageBase64" },
                Exports().Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("Data Copier", Metadata("Name"));

            string description = Metadata("Description");
            Assert.Contains("keeping the same GUIDs", description);
            Assert.Contains("lookups", description);
            Assert.Contains("child records", description);
            Assert.EndsWith(".", description);
            Assert.Equal(1, description.Count(c => c == '.'));   // one sentence
        }

        [Theory]
        [InlineData("SmallImageBase64", 32)]
        [InlineData("BigImageBase64", 120)]
        public void The_tool_has_dedicated_small_and_large_images(string key, int side)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(Metadata(key))))
            using (Image image = Image.FromStream(stream))
            {
                Assert.Equal(ImageFormat.Png.Guid, image.RawFormat.Guid);
                Assert.Equal(new Size(side, side), image.Size);
            }
        }

        [Theory]
        [InlineData("icon-32.png", 32)]
        [InlineData("icon-80.png", 80)]
        [InlineData("icon-128.png", 128)]
        public void The_repository_icons_are_PNGs_of_their_size(string file, int side)
        {
            byte[] png = File.ReadAllBytes(FindInRepository(Path.Combine("images", file)));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8));
            Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
            Assert.Equal((side, side), (BigEndian(png, 16), BigEndian(png, 20)));   // width and height in the header
            if (side == 32) Assert.Equal(Convert.FromBase64String(Metadata("SmallImageBase64")), png);   // the small image itself
        }

        private static int BigEndian(byte[] bytes, int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];

        [Fact]
        public void The_assembly_names_the_tool_and_its_publisher_and_has_one_version_equal_to_the_package_version()
        {
            Assembly assembly = typeof(MyscotekDataCopierPlugin).Assembly;
            Assert.Equal("MyscotekDataCopier", assembly.GetName().Name);
            Assert.Equal("Data Copier", assembly.GetCustomAttribute<AssemblyTitleAttribute>().Title);
            Assert.Equal("Data Copier", assembly.GetCustomAttribute<AssemblyProductAttribute>().Product);
            Assert.Equal("Myscotek", assembly.GetCustomAttribute<AssemblyCompanyAttribute>().Company);   // XrmToolBox requires it
            Assert.Equal("Copyright (c) 2026 Myscotek", assembly.GetCustomAttribute<AssemblyCopyrightAttribute>().Copyright);
            Assert.Equal(Metadata("Description"), assembly.GetCustomAttribute<AssemblyDescriptionAttribute>().Description);

            // The Tool Library compares the package version with the assembly version: all must agree.
            string version = assembly.GetName().Version.ToString();
            Assert.Matches(@"^1\.20\d\d\.(1[0-2]|[1-9])\.\d+$", version);   // date style: 1.YYYY.M.N
            Assert.Equal(version, assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version);
            Assert.Equal(version, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion);
            XNamespace nuspec = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";
            XElement metadata = XDocument.Load(FindInRepository("Myscotek.DataCopier.nuspec")).Root.Element(nuspec + "metadata");
            Assert.Equal(version, (string)metadata.Element(nuspec + "version"));
            Assert.Equal("Data Copier", (string)metadata.Element(nuspec + "title"));
        }

        [Fact]
        public void The_control_links_XrmToolBox_to_the_public_repository_and_its_readme()
        {
            Assert.True(typeof(IGitHubPlugin).IsAssignableFrom(typeof(DataCopierControl)));
            Assert.True(typeof(IHelpPlugin).IsAssignableFrom(typeof(DataCopierControl)));
            UiTestHost.Run(() =>
            {
                using (DataCopierControl control = UiTest.NewControl(new DataCopierSettings()))
                {
                    var gitHub = (IGitHubPlugin)control;
                    Assert.Equal("scottm1985", gitHub.UserName);
                    Assert.Equal("DataCopier", gitHub.RepositoryName);
                    Assert.Equal("https://github.com/scottm1985/DataCopier#readme", ((IHelpPlugin)control).HelpUrl);
                    Assert.Equal("Data Copier", DataCopierControl.DialogTitle);   // message box captions
                }
            });
        }

        /// <summary>A file at the repository root, found by walking up from the test output folder.</summary>
        private static string FindInRepository(string fileName)
        {
            for (var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); folder != null; folder = folder.Parent)
            {
                string path = Path.Combine(folder.FullName, fileName);
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException(fileName + " was not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
