namespace CodeIndex.Models;

/// <summary>
/// Declaration-local framework-invocation markers for a <see cref="MemberInfo"/>. A set bit means the member is
/// invoked by a framework (WCF dispatcher, serializer, xUnit, the OS SCM, the ASP.NET pipeline) with no ordinary
/// C# caller, so a dead-code / <c>likely_unused</c> pass must treat it as used. Folded onto the existing member
/// record (a single byte) rather than stored as separate records — the high-frequency roots (<c>[DataMember]</c>,
/// <c>[Fact]</c>, <c>[OperationContract]</c>) number in the thousands and discrete records would bloat the cache.
/// APPEND-ONLY bit values — never renumber (MessagePack stores the underlying integer).
/// </summary>
[Flags]
public enum MemberRootKind
{
    None = 0,
    /// <summary><c>[OperationContract]</c> on a WCF service member — invoked by the WCF dispatcher / TS frontend.</summary>
    WcfOperation = 1 << 0,
    /// <summary><c>[OnDeserialized]</c>/<c>[OnSerializing]</c>/… serialization lifecycle callback.</summary>
    SerializationCallback = 1 << 1,
    /// <summary><c>[DataMember]</c> — setter written by the serializer via reflection.</summary>
    DataMember = 1 << 2,
    /// <summary><c>[Fact]</c>/<c>[Theory]</c> (or NUnit/MSTest) test entry point.</summary>
    Test = 1 << 3,
    /// <summary><c>IHttpHandler.ProcessRequest</c>/<c>IsReusable</c> convention member.</summary>
    HttpHandlerMethod = 1 << 4,
    /// <summary><c>ServiceBase.OnStart</c>/<c>OnStop</c>/… Windows-service lifecycle override.</summary>
    ServiceControlMethod = 1 << 5,
    /// <summary>Process entry point <c>static Main</c>.</summary>
    EntryPointMain = 1 << 6,
    /// <summary><c>HttpApplication</c> lifecycle handler (<c>Application_Start</c>, <c>Application_Error</c>, …).</summary>
    HttpAppLifecycle = 1 << 7,
}

/// <summary>
/// Declaration-local framework-role markers for a <see cref="TypeInfo"/>. A set bit means the type is a framework
/// root (its instances are created / dispatched by a framework), so it and its convention members must not be
/// reported dead. APPEND-ONLY bit values.
/// </summary>
[Flags]
public enum TypeRootKind
{
    None = 0,
    /// <summary><c>[ServiceContract]</c> interface/type — a WCF contract.</summary>
    WcfServiceContract = 1 << 0,
    /// <summary><c>[ServiceBehavior]</c>/<c>[AspNetCompatibilityRequirements]</c> or implements a <c>[ServiceContract]</c>.</summary>
    WcfServiceImpl = 1 << 1,
    /// <summary>Implements <c>IHttpHandler</c>/<c>IHttpAsyncHandler</c> (possibly transitively).</summary>
    HttpHandler = 1 << 2,
    /// <summary>Derives from <c>System.Web.HttpApplication</c> (Global.asax).</summary>
    HttpApplication = 1 << 3,
    /// <summary>Derives from <c>ServiceBase</c> (Windows service).</summary>
    ServiceBase = 1 << 4,
    /// <summary><c>[DataContract]</c> — instantiated/populated by the serializer.</summary>
    DataContract = 1 << 5,
    /// <summary>A test class (contains <c>[Fact]</c>/<c>[Theory]</c> members or an xUnit fixture).</summary>
    TestClass = 1 << 6,
}
