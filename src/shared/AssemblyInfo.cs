using System.Reflection;
using System.Runtime.Versioning;

// Without this the runtime treats each exe as a .NET 4.0 app and keeps old compatibility behaviour
// (no TLS 1.2 by default, no per-monitor DPI). MSBuild normally generates it; csc alone does not.
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyProduct("Capsule")]
[assembly: AssemblyVersion("0.1.0.0")]
