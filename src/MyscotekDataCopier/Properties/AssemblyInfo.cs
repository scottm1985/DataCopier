using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Data Copier")]
[assembly: AssemblyDescription("Copies selected records from a source to a destination Dataverse / Dynamics 365 environment keeping the same GUIDs, with the records their lookups point at and, optionally, their child records and N:N associations.")]
[assembly: AssemblyProduct("Data Copier")]
// Required: XrmToolBox reads AssemblyCompany with an unguarded GetCustomAttributes(...)[0] when it
// builds its plugin manifest; without it the host throws on startup. GenerateAssemblyInfo is off,
// so it must be declared here.
[assembly: AssemblyCompany("Myscotek")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Myscotek")]
[assembly: ComVisible(false)]
[assembly: InternalsVisibleTo("MyscotekDataCopier.Tests")]
// The release version, date style (1.YYYY.M.N). Change all three together with <version> in
// Myscotek.DataCopier.nuspec: the XrmToolBox Tool Library compares the package version with the
// assembly version (a mismatch shows a never-ending update), and XrmToolBox re-reads a plugin's
// metadata only when its AssemblyVersion changes. build-package.ps1 refuses to pack a mismatch.
[assembly: AssemblyVersion("1.2026.10.3")]
[assembly: AssemblyFileVersion("1.2026.10.3")]
[assembly: AssemblyInformationalVersion("1.2026.10.3")]
