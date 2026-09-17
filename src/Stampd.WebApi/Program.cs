using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Scalar.AspNetCore;

using Serilog;

using Stampd.Core;
using Stampd.Core.Revocation;
using Stampd.Core.Sealing;
using Stampd.Core.Tenancy;
using Stampd.Crypto.AwsKms;
using Stampd.Crypto.AzureKeyVault;
using Stampd.Crypto.LocalCertificate;
using Stampd.Crypto.Vault;
using Stampd.Email.Smtp;
using Stampd.Engine;
using Stampd.Engine.Rendering;
using Stampd.Identity.EmailOtp;
using Stampd.Identity.Oidc;
using Stampd.Identity.Saml2;
using Stampd.Infrastructure;
using Stampd.Infrastructure.Identity;
using Stampd.Infrastructure.Sqlite;
using Stampd.Revocation.Http;
using Stampd.Storage.AzureBlob;
using Stampd.Storage.FileSystem;
using Stampd.Storage.Gcs;
using Stampd.Storage.S3;
using Stampd.Timestamp.FreeTsa;
using Stampd.Timestamp.Rfc3161;
using Stampd.WebApi.Auth;
using Stampd.WebApi.Endpoints;
using Stampd.WebApi.Observability;
using Stampd.WebApi.Observability.HealthChecks;
using Stampd.WebApi.Services;
using Stampd.WebApi.Tenancy;

// PdfSharp 6.x has no default font resolver outside Windows — register at process start.
PlatformFontResolver.Register();

var builder = WebApplication.CreateBuilder(args);

// ---- Observability: Serilog FIRST so config loading is captured ----
builder.ConfigureSerilog();

// OpenTelemetry traces + metrics
builder.Services.AddStampdOpenTelemetry(builder.Configuration);

// ---- Configuration ----
var certPath = builder.Configuration["Stampd:SigningCertificate:Pkcs12Path"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "spike-signer.pfx");
var certPassword = builder.Configuration["Stampd:SigningCertificate:Password"] ?? "stampd-spike";
var enableTsa = builder.Configuration.GetValue("Stampd:Tsa:Enabled", defaultValue: true);
var tsaEndpoint = builder.Configuration["Stampd:Tsa:Endpoint"];
var storageRoot = builder.Configuration["Stampd:Storage:FileSystemRoot"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "documents");
var sqlitePath = builder.Configuration["Stampd:Database:SqlitePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "stampd.db");

Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
Directory.CreateDirectory(storageRoot);

// ---- Services ----
builder.Services.AddOpenApi();

// Persistence
builder.Services.AddStampdSqlite($"Data Source={sqlitePath}");

// Document storage: FileSystem (default, dev), S3, AzureBlob, or GCS.
var storageProvider = builder.Configuration["Stampd:Storage:Provider"] ?? "FileSystem";
if (string.Equals(storageProvider, "S3", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddS3DocumentStorage(options =>
    {
        options.BucketName = builder.Configuration["Stampd:Storage:S3:BucketName"]
            ?? throw new InvalidOperationException("Storage:S3:BucketName required");
        options.Region = builder.Configuration["Stampd:Storage:S3:Region"];
        options.KeyPrefix = builder.Configuration["Stampd:Storage:S3:KeyPrefix"];
        options.KmsKeyId = builder.Configuration["Stampd:Storage:S3:KmsKeyId"];
        var serviceUrl = builder.Configuration["Stampd:Storage:S3:ServiceUrl"];
        if (!string.IsNullOrEmpty(serviceUrl))
        {
            options.ServiceUrl = new Uri(serviceUrl);
        }
    });
}
else if (string.Equals(storageProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAzureBlobDocumentStorage(options =>
    {
        options.AccountUri = new Uri(builder.Configuration["Stampd:Storage:AzureBlob:AccountUri"]
            ?? throw new InvalidOperationException("Storage:AzureBlob:AccountUri required"));
        options.ContainerName = builder.Configuration["Stampd:Storage:AzureBlob:ContainerName"]
            ?? throw new InvalidOperationException("Storage:AzureBlob:ContainerName required");
        options.KeyPrefix = builder.Configuration["Stampd:Storage:AzureBlob:KeyPrefix"];
        options.EncryptionScope = builder.Configuration["Stampd:Storage:AzureBlob:EncryptionScope"];
    });
}
else if (string.Equals(storageProvider, "GCS", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddGcsDocumentStorage(options =>
    {
        options.BucketName = builder.Configuration["Stampd:Storage:Gcs:BucketName"]
            ?? throw new InvalidOperationException("Storage:Gcs:BucketName required");
        options.KeyPrefix = builder.Configuration["Stampd:Storage:Gcs:KeyPrefix"];
        options.KmsKeyName = builder.Configuration["Stampd:Storage:Gcs:KmsKeyName"];
    });
}
else
{
    builder.Services.AddFileSystemDocumentStorage(storageRoot);
}

// Tenant context (header-based)
var defaultTenantId = Guid.Parse(
    builder.Configuration["Stampd:Tenancy:DefaultTenantId"] ?? "00000000-0000-0000-0000-000000000001");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext>(sp =>
    new HttpTenantContext(sp.GetRequiredService<IHttpContextAccessor>(), defaultTenantId));

// Sealing: LocalCertificate, Vault, or AzureKeyVault by config.
var sealingProvider = builder.Configuration["Stampd:Sealing:Provider"] ?? "Local";
if (string.Equals(sealingProvider, "Vault", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddVaultSealing(options =>
    {
        options.VaultAddress = new Uri(builder.Configuration["Stampd:Sealing:Vault:Address"]
            ?? throw new InvalidOperationException("Vault:Address required"));

        // Auth method selection: Token (default, dev), AppRole (production VMs/CI),
        // Kubernetes (production K8s workloads). VaultSharp handles lease renewal
        // automatically for AppRole and Kubernetes.
        var authMethod = builder.Configuration["Stampd:Sealing:Vault:AuthMethod"] ?? "Token";
        options.AuthMethod = Enum.Parse<VaultAuthMethod>(authMethod, ignoreCase: true);
        options.Token = builder.Configuration["Stampd:Sealing:Vault:Token"];
        options.AppRoleId = builder.Configuration["Stampd:Sealing:Vault:AppRoleId"];
        options.AppRoleSecretId = builder.Configuration["Stampd:Sealing:Vault:AppRoleSecretId"];
        options.AppRoleMountPath = builder.Configuration["Stampd:Sealing:Vault:AppRoleMountPath"]
            ?? "approle";
        options.KubernetesRole = builder.Configuration["Stampd:Sealing:Vault:KubernetesRole"];
        options.KubernetesServiceAccountTokenPath =
            builder.Configuration["Stampd:Sealing:Vault:KubernetesServiceAccountTokenPath"]
            ?? "/var/run/secrets/kubernetes.io/serviceaccount/token";
        options.KubernetesMountPath = builder.Configuration["Stampd:Sealing:Vault:KubernetesMountPath"]
            ?? "kubernetes";

        options.TransitMountPath = builder.Configuration["Stampd:Sealing:Vault:TransitMountPath"] ?? "transit";
        options.KeyName = builder.Configuration["Stampd:Sealing:Vault:KeyName"];
        options.CertificatePath = builder.Configuration["Stampd:Sealing:Vault:CertificatePath"];
    });
}
else if (string.Equals(sealingProvider, "AzureKeyVault", StringComparison.OrdinalIgnoreCase))
{
    // Azure Key Vault: uses DefaultAzureCredential which chains environment vars,
    // Managed Identity, Azure CLI, etc. The signing key never leaves the vault; the
    // cert (public part) is downloaded once and cached.
    builder.Services.AddAzureKeyVaultSealing(options =>
    {
        options.KeyIdentifier = new Uri(builder.Configuration["Stampd:Sealing:AzureKeyVault:KeyIdentifier"]
            ?? throw new InvalidOperationException("AzureKeyVault:KeyIdentifier required"));
        options.CertificateVaultUri = new Uri(builder.Configuration["Stampd:Sealing:AzureKeyVault:CertificateVaultUri"]
            ?? throw new InvalidOperationException("AzureKeyVault:CertificateVaultUri required"));
        options.CertificateName = builder.Configuration["Stampd:Sealing:AzureKeyVault:CertificateName"]
            ?? throw new InvalidOperationException("AzureKeyVault:CertificateName required");
    });
}
else if (string.Equals(sealingProvider, "AwsKms", StringComparison.OrdinalIgnoreCase))
{
    // AWS KMS: auth via the AWS SDK default credential chain (env vars, shared profile,
    // EC2 instance metadata, ECS task role, EKS IRSA / Pod Identity). The cert (public
    // part) lives on disk alongside the running service; KMS holds the private key.
    builder.Services.AddAwsKmsSealing(options =>
    {
        options.KeyId = builder.Configuration["Stampd:Sealing:AwsKms:KeyId"]
            ?? throw new InvalidOperationException("AwsKms:KeyId required");
        options.Region = builder.Configuration["Stampd:Sealing:AwsKms:Region"];
        options.CertificatePath = builder.Configuration["Stampd:Sealing:AwsKms:CertificatePath"]
            ?? throw new InvalidOperationException("AwsKms:CertificatePath required");
    });
}
else
{
    builder.Services.AddLocalCertificateSealing(_ =>
    {
        var (cert, _) = SelfSignedCertificateFactory.CreateOrLoad(certPath, certPassword);
        return cert;
    });
}

if (enableTsa)
{
    // TSA provider selection:
    //   - "FreeTSA" (default) → free public TSA, dev / demo / low-volume.
    //   - "Rfc3161"           → generic provider, configurable URL + optional basic auth +
    //                           optional mTLS client cert. Use for DigiCert / GlobalSign /
    //                           Sectigo / internal Microsoft AD CS / EJBCA TSAs.
    var tsaProvider = builder.Configuration["Stampd:Tsa:Provider"] ?? "FreeTSA";
    if (string.Equals(tsaProvider, "Rfc3161", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddRfc3161TimestampAuthority(options =>
        {
            options.Name = builder.Configuration["Stampd:Tsa:Name"] ?? "RFC3161";
            options.Endpoint = new Uri(builder.Configuration["Stampd:Tsa:Endpoint"]
                ?? throw new InvalidOperationException(
                    "Stampd:Tsa:Endpoint is required when Stampd:Tsa:Provider = 'Rfc3161'."));
            options.BasicAuthUsername = builder.Configuration["Stampd:Tsa:BasicAuthUsername"];
            options.BasicAuthPassword = builder.Configuration["Stampd:Tsa:BasicAuthPassword"];
            options.ClientCertificatePkcs12Path = builder.Configuration["Stampd:Tsa:ClientCertificatePkcs12Path"];
            options.ClientCertificatePassword = builder.Configuration["Stampd:Tsa:ClientCertificatePassword"];
            options.RequestedPolicyOid = builder.Configuration["Stampd:Tsa:RequestedPolicyOid"];
            options.RequestTsaCertificate = builder.Configuration.GetValue(
                "Stampd:Tsa:RequestTsaCertificate", defaultValue: true);
            options.IncludeNonce = builder.Configuration.GetValue(
                "Stampd:Tsa:IncludeNonce", defaultValue: true);
        });
    }
    else
    {
        builder.Services.AddFreeTsaTimestampAuthority(tsaEndpoint is null ? null : new Uri(tsaEndpoint));
    }

    // v2.1 — opt-in DigiCert failover. When Stampd:Tsa:EnableDigiCertFailover=true,
    // we wrap whatever ITimestampAuthorityProvider was just registered with a
    // FailoverTimestampAuthorityProvider that prepends the primary and falls
    // back to DigiCert's free public TSA if the primary throws / times out /
    // refuses. Default is FALSE so existing deployments see zero behaviour
    // change. Adopters running FreeTSA in dev — where it flakes — get a
    // one-line config knob to make the demo reliable.
    var enableDigiCertFailover = builder.Configuration.GetValue(
        "Stampd:Tsa:EnableDigiCertFailover", defaultValue: false);
    if (enableDigiCertFailover)
    {
        builder.Services.AddHttpClient("stampd-tsa-failover-digicert", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        // The trick: capture the singleton already registered above, then
        // replace its registration with one that wraps it. ServiceDescriptor
        // .DescribeKeyed isn't needed because there's only one provider
        // registration for the interface — the last one wins via
        // ServiceCollection.Replace.
        builder.Services.AddSingleton<FailoverTsaWrapperFactory>();
        var primaryDescriptor = builder.Services.Last(
            d => d.ServiceType == typeof(ITimestampAuthorityProvider));
        builder.Services.Remove(primaryDescriptor);
        builder.Services.AddSingleton<ITimestampAuthorityProvider>(sp =>
        {
            // Re-resolve the primary by hand using the original descriptor.
            // We can't just call sp.GetService<ITimestampAuthorityProvider>()
            // because that would loop back into this factory.
            var primary = ResolvePrimary(sp, primaryDescriptor);
            var factory = sp.GetRequiredService<FailoverTsaWrapperFactory>();
            return factory.WrapWithDigiCert(primary);
        });
    }
}

// Local helper — invokes the original primary-TSA descriptor without going
// through DI (avoids self-recursion in the wrapped registration above).
static ITimestampAuthorityProvider ResolvePrimary(IServiceProvider sp, ServiceDescriptor descriptor)
{
    if (descriptor.ImplementationFactory is not null)
    {
        return (ITimestampAuthorityProvider)descriptor.ImplementationFactory(sp);
    }
    if (descriptor.ImplementationInstance is ITimestampAuthorityProvider instance)
    {
        return instance;
    }
    if (descriptor.ImplementationType is not null)
    {
        return (ITimestampAuthorityProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
    }
    throw new InvalidOperationException(
        "Cannot resolve the primary ITimestampAuthorityProvider for failover wrapping.");
}

// Revocation providers for PAdES B-LT. When enabled, the engine pre-fetches OCSP / CRL
// material for each cert in the signer chain and embeds it in a /DSS catalog entry.
// Adopters who don't need B-LT can leave this disabled — engine output stays at B-T.
var enableRevocation = builder.Configuration.GetValue("Stampd:Revocation:Enabled", defaultValue: false);
if (enableRevocation)
{
    builder.Services.AddHttpRevocationProviders();
}

// Process-wide PAdES level. Endpoints read this via PadesDefaults so callers don't have to
// pass it in every request body. Adopters who want B-LT set Stampd:Pades:TargetLevel="BLT"
// (and Stampd:Revocation:Enabled=true). For B-LTA set it to "BLTA" — adds a second TSA
// round-trip per signature for the archive timestamp.
var padesLevel = Enum.TryParse<Stampd.Core.Entities.PAdESLevel>(
    builder.Configuration["Stampd:Pades:TargetLevel"], ignoreCase: true, out var parsedLevel)
    ? parsedLevel
    : Stampd.Core.Entities.PAdESLevel.BT;
builder.Services.AddSingleton(new PadesDefaults(padesLevel));

builder.Services.AddSingleton<IStampdEngine>(sp =>
{
    var sealing = sp.GetRequiredService<ICryptographicSealingProvider>();
    var tsa = sp.GetService<ITimestampAuthorityProvider>();
    var revocation = sp.GetService<IRevocationProvider>();
    return new PdfSharpStampdEngine(sealing, tsa, revocation);
});

builder.Services.AddScoped<SigningWorkflowService>();
builder.Services.AddScoped<WebhookDispatcher>();
// v1.3 #134 — completion email notifier. Singleton because it holds no per-request state;
// the workflow service captures it via constructor injection alongside the existing
// IEmailSender / WorkflowEmailOptions surface.
builder.Services.AddSingleton<SenderCompletionNotifier>();

// Workflow email defaults — pulled from config so adopters can flip the dispatch path
// on by setting Stampd:Workflow:Email:SigningUrlTemplate without changing code.
// SigningUrlTemplate default: in Development point at the local Blazor UI's signer page
// (5170/sign/{token}), so the access URLs the API returns are clickable without any extra
// config. Adopters override Stampd:Workflow:Email:SigningUrlTemplate for production.
var defaultSigningUrlTemplate = builder.Environment.IsDevelopment()
    ? "http://localhost:5190/sign/{accessToken}"
    : null;

// SignedDocumentUrlTemplate default (v1.3 #134): point senders at the Blazor UI's signed
// documents listing in Development, so the completion email's CTA is clickable end-to-end
// out of the box. Adopters override Stampd:Workflow:Email:SignedDocumentUrlTemplate for
// production deployments.
var defaultSignedDocumentUrlTemplate = builder.Environment.IsDevelopment()
    ? "http://localhost:5190/signed/{signedDocumentId}"
    : null;

builder.Services.AddSingleton(new WorkflowEmailOptions
{
    FromAddress = builder.Configuration["Stampd:Workflow:Email:FromAddress"] ?? "noreply@stampd.local",
    FromDisplayName = builder.Configuration["Stampd:Workflow:Email:FromDisplayName"] ?? "Stampd",
    SigningUrlTemplate = builder.Configuration["Stampd:Workflow:Email:SigningUrlTemplate"] ?? defaultSigningUrlTemplate,
    SignedDocumentUrlTemplate = builder.Configuration["Stampd:Workflow:Email:SignedDocumentUrlTemplate"] ?? defaultSignedDocumentUrlTemplate,
    ProductName = builder.Configuration["Stampd:Workflow:Email:ProductName"] ?? "Stampd",
});

// ---- Email transport ----
// SMTP sender. Suitable for any RFC 5321 server (SendGrid, AWS SES, Postmark, Mailgun, or local SMTP).
// Configured via Stampd:Email:Smtp:*.
builder.Services.AddSmtpEmailSender(opts =>
{
    opts.Host = builder.Configuration["Stampd:Email:Smtp:Host"] ?? "localhost";
    opts.Port = builder.Configuration.GetValue("Stampd:Email:Smtp:Port", 587);
    opts.Username = builder.Configuration["Stampd:Email:Smtp:Username"];
    opts.Password = builder.Configuration["Stampd:Email:Smtp:Password"];
    var sec = builder.Configuration["Stampd:Email:Smtp:Security"];
    if (!string.IsNullOrWhiteSpace(sec)
        && Enum.TryParse<MailKit.Security.SecureSocketOptions>(sec, ignoreCase: true, out var parsedSec))
    {
        opts.Security = parsedSec;
    }
    else
    {
        opts.Security = MailKit.Security.SecureSocketOptions.Auto;
    }
});

// ---- Identity verification (Email OTP) ----
// Persistent OTP store FIRST so the in-memory default doesn't win the TryAddSingleton race.
builder.Services.AddDbOtpChallengeStore();
builder.Services.AddEmailOtpIdentityVerification(opts =>
{
    opts.FromAddress = builder.Configuration["Stampd:Identity:EmailOtp:FromAddress"]
        ?? builder.Configuration["Stampd:Workflow:Email:FromAddress"]
        ?? "noreply@stampd.local";
    opts.FromDisplayName = builder.Configuration["Stampd:Identity:EmailOtp:FromDisplayName"]
        ?? builder.Configuration["Stampd:Workflow:Email:FromDisplayName"]
        ?? "Stampd";
    opts.ProductName = builder.Configuration["Stampd:Identity:EmailOtp:ProductName"]
        ?? builder.Configuration["Stampd:Workflow:Email:ProductName"]
        ?? "Stampd";
    var lifetimeMinutes = builder.Configuration.GetValue("Stampd:Identity:EmailOtp:ChallengeLifetimeMinutes", 10);
    opts.ChallengeLifetime = TimeSpan.FromMinutes(lifetimeMinutes);
});

// Background workers: bulk-send dispatcher and webhook delivery outbox drainer. Both
// poll the DB; both gracefully cross-tenant via TenantScope.
builder.Services.AddHostedService<BulkSendWorker>();
builder.Services.AddHostedService<WebhookDeliveryWorker>();
builder.Services.AddHttpClient(nameof(WebhookDeliveryWorker));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ---- Health checks ----
builder.Services.AddHealthChecks()
    .AddDbContextCheck<StampdDbContext>(
        name: "database",
        tags: ["live", "ready"])
    .AddCheck<SealingProviderHealthCheck>(
        name: "sealing-provider",
        tags: ["ready"])
    .AddCheck<TimestampAuthorityHealthCheck>(
        name: "timestamp-authority",
        failureStatus: HealthStatus.Degraded,
        tags: ["ready"]);

// ---- AuthN/AuthZ ----
//
// IMPORTANT: bind JwtOptions via the DI options pattern (NOT by reading
// builder.Configuration["..."] eagerly here). WebApplicationFactory adds its in-memory
// config overrides via ConfigureAppConfiguration which runs DURING builder.Build() —
// later than the top-level statements in Program.cs. If we read the JWT issuer/audience
// here, integration tests would get the appsettings.json defaults instead of their
// overrides, and every JWT would fail validation with 401.
//
// The fix: register JwtBearerOptions configuration as a DI callback that runs at
// resolve-time via IOptionsMonitor<JwtOptions>, so it sees the final, fully-merged config.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Stampd:Auth:Jwt"));

// v3.0 alpha.2 — auth-mode selector. DevJwt mode keeps v2.x behavior (in-process
// signing + dev token endpoint). Oidc mode hands JwtBearer over to an external
// IdP via Stampd.Identity.Oidc.AddStampdOidcRelay. Default is DevJwt to preserve
// existing local dev flows; production deployments MUST opt into Oidc and the
// guard below throws if they don't.
//
// Read as a string then case-insensitive parse — GetValue<TEnum> binding for
// in-memory config providers has been flaky across .NET 10 preview builds.
// Manual string compare is rock-solid and avoids surprises.
var authModeRaw = builder.Configuration["Stampd:Auth:Mode"];
// v3.0 alpha.3 — third mode 'Saml2' for SAML federation. SAML2 alpha is scaffold
// only (config + endpoint surface + claim mapper). v3.0.0 stable wires the real
// ITfoxtec.Identity.Saml2-backed assertion validation.
var isOidc = string.Equals(authModeRaw, "Oidc", StringComparison.OrdinalIgnoreCase);
var isSaml2 = string.Equals(authModeRaw, "Saml2", StringComparison.OrdinalIgnoreCase);

if (!isOidc && !isSaml2
    && !builder.Environment.IsDevelopment()
    && builder.Environment.EnvironmentName != "Testing")
{
    throw new InvalidOperationException(
        "Stampd:Auth:Mode=DevJwt is only allowed in Development. Set Stampd:Auth:Mode=Oidc " +
        "(real-IdP OIDC validation) or Saml2 (SAML2 SP scaffold) for production deployments.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

if (isSaml2)
{
    // v3.0 alpha.3 — register the SAML2 SP scaffold's eager config validation.
    // Endpoint scaffolds map at app-bootstrap time below.
    builder.Services.AddStampdSaml2Sp(builder.Configuration.GetSection("Stampd:Auth:Saml2"));
}

if (isOidc)
{
    // OIDC relay wires JwtBearer against the external IdP's discovery document
    // and applies Stampd's role-claim mapping at token-validated time. Throws at
    // startup if Authority/Audience are missing.
    builder.Services.AddStampdOidcRelay(builder.Configuration.GetSection("Stampd:Auth:Oidc"));
}
else
{
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptionsMonitor<JwtOptions>>((bearerOptions, jwtMonitor) =>
        {
            var jwt = jwtMonitor.CurrentValue;
            bearerOptions.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            };
        });
}

// v2.0 Slice D — server-side actor context for audit attribution. Scoped so each
// HTTP request gets a fresh read of HttpContext.User. Background workers and tests
// that don't have an HttpContext get IsAuthenticated=false / UserId=null which is
// the correct shape for "system-initiated action".
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Stampd.Core.Authorization.ICurrentActorContext,
    HttpCurrentActorContext>();

// v2.0 — three named policies map onto the StampdRoles taxonomy. Endpoint groups gate
// themselves on the appropriate policy (see managementGroup / adminGroup wiring below).
// Default fallback policy is RequireAuthenticatedUser so any endpoint that forgets to
// tag a policy still requires a valid JWT — defense-in-depth.
// v3.0 alpha.1 — register the per-tenant Admin scope handler. The policy itself
// only carries the Admin scope requirement; the handler walks the AdminScopes
// table to validate. Scoped registration so each request gets a handler bound to
// the per-request ICurrentActorContext + ITenantContext.
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    Stampd.WebApi.Auth.AdminScopeAuthorizationHandler>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Stampd.Core.Authorization.StampdRoles.Admin, p => p
        .RequireAuthenticatedUser()
        // v3.0 alpha.1: was .RequireRole(Admin). The new requirement checks BOTH
        // the Admin role claim AND an active AdminScope row for the request's
        // tenant. The handler enforces both gates so existing role-aware code (UI
        // nav, dev JWT minter, etc.) doesn't need changes.
        .AddRequirements(new Stampd.WebApi.Auth.AdminScopeRequirement()))
    .AddPolicy("SenderOrAdmin", p => p
        .RequireAuthenticatedUser()
        .RequireRole(
            Stampd.Core.Authorization.StampdRoles.Sender,
            Stampd.Core.Authorization.StampdRoles.Admin))
    .AddPolicy("AuthenticatedAny", p => p
        .RequireAuthenticatedUser())
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// ---- Rate limiting ----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sign", httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = 10,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

// ---- Request size caps (50 MB) ----
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
});

var app = builder.Build();

// ---- Apply migrations on startup (dev convenience) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

// ---- Pipeline ----
app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Stampd API");
        options.WithTheme(ScalarTheme.BluePlanet);
    });
}

// HTTPS redirection in production is typically handled at the load balancer / ingress.
// In-process redirection in the Testing environment causes a 307 that the test HttpClient
// follows, stripping the Authorization header in the process — every protected-endpoint
// test then fails with 401. Skip the middleware when running under WebApplicationFactory.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---- Endpoints ----
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponseAsync,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponseAsync,
}).AllowAnonymous();

// Endpoint extension methods return IEndpointRouteBuilder, which can't chain
// RequireAuthorization / AllowAnonymous / RequireRateLimiting directly. Wrap each
// extension call in a MapGroup that pre-applies the policy — the group's conventions
// propagate to every endpoint mapped inside it.

// Anonymous surface: health, dev token issuance, recipient signing (recipient is
// authenticated by the per-recipient access token in the URL, not by JWT).
var anonGroup = app.MapGroup("").AllowAnonymous();
anonGroup.MapHealth();
anonGroup.MapAuth();
anonGroup.MapRecipientSigning();
// Demo bootstrap — registers /api/demo/seed only in Development.
anonGroup.MapDemo(app.Environment);

// Sign endpoints: rate-limited AND require auth. v2.0 — gate on SenderOrAdmin so
// view-only users can't submit signatures.
var signGroup = app.MapGroup("")
    .RequireAuthorization("SenderOrAdmin")
    .RequireRateLimiting("sign");
signGroup.MapSign();
signGroup.MapBinarySign();

// Management endpoints: any authenticated user can read; write semantics get enforced
// inside each endpoint handler as v2.0 layers in. The group-level policy is
// AuthenticatedAny so ReadOnly users hit the same surface for listing/viewing — Slice D
// will introduce per-action policy checks (void / regenerate / delete) inside the
// handlers when bulk operations land.
var managementGroup = app.MapGroup("").RequireAuthorization("AuthenticatedAny");
managementGroup.MapTemplates();
managementGroup.MapSigningRequests();
managementGroup.MapSignedDocuments();
managementGroup.MapBulkSend();
managementGroup.MapWebhooks();

// v2.0 — admin-only surface. Slice A maps /api/admin/dashboard, /api/admin/dashboard/trend,
// /api/admin/dashboard/top-templates here. Slices B/C/D extend with bulk operations and
// cross-sender analytics.
var adminGroup = app.MapGroup("").RequireAuthorization(Stampd.Core.Authorization.StampdRoles.Admin);
adminGroup.MapAdminDashboard();
adminGroup.MapAdminOperations();
adminGroup.MapAdminAnalytics();
adminGroup.MapAdminAuditExport();
adminGroup.MapAdminScopes();

// v3.0 alpha.3 — SAML2 endpoint scaffolds. Always mapped (regardless of Mode)
// so adopters can probe `/api/auth/saml/metadata` to verify reverse-proxy /
// DNS routing even before flipping Mode=Saml2. Bodies 501 until v3.0.0 stable.
app.MapStampdSaml2Endpoints();

try
{
    Log.Information("Stampd.WebApi starting on {EnvName}", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Stampd.WebApi terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
