using System.Reflection;

// Assembly identity shared by every project in the solution. This file is linked into each
// project via <Compile Include="..\SharedVersion.cs" />, so a project added later inherits the
// product branding automatically instead of carrying its own copy that can drift back to the
// upstream name during a merge.
//
// AssemblyProduct is user-visible: ApplicationHost reads it back through
// FileVersionInfo.ProductName and serves it as ProductName on /System/Info and
// /System/Info/Public, so every connected client displays it.
//
// Per-assembly attributes (AssemblyTitle, AssemblyDescription) and the GPL-mandated
// AssemblyCopyright stay in each project's Properties/AssemblyInfo.cs.
[assembly: AssemblyVersion("12.0.0")]
[assembly: AssemblyFileVersion("12.0.0")]
[assembly: AssemblyCompany("SlopTank")]
[assembly: AssemblyProduct("SlopTank Server")]
