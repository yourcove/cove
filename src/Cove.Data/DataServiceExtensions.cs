using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Npgsql;
using Pgvector;

namespace Cove.Data;

public static class DataServiceExtensions
{
    public static IServiceCollection AddCoveData(this IServiceCollection services, string connectionString)
    {
        // Segment span projection services are part of the data layer and share this host cache.
        // Register it here so AddCoveData remains a complete composition unit outside Cove.Api.
        services.AddMemoryCache();

        services.AddSingleton(sp =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            dataSourceBuilder.UseVector();
            return dataSourceBuilder.Build();
        });

        // Not pooled by design: a data extension installed at runtime changes the EF model (it
        // contributes new entity types via CoveContext.OnModelCreating). Pooled context instances pin
        // the model they first resolved, so a model rebuild would never reach already-rented instances
        // and the extension's DbSet<> types would fail until an app restart. Non-pooled contexts resolve
        // the current model per scope, so paired with CoveModelCacheKeyFactory (keyed on
        // CoveContext.ModelGeneration) runtime install/uninstall takes effect with no restart. Context
        // construction is cheap relative to Cove's query workload; the model itself is still cached per
        // generation, so it is rebuilt once on change, not per request.
        services.AddDbContext<CoveContext>((sp, options) =>
        {
            var dataSource = sp.GetRequiredService<NpgsqlDataSource>();

            options.UseNpgsql(dataSource, npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
                npgsqlOptions.MigrationsAssembly(typeof(CoveContext).Assembly.FullName);
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                npgsqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
            });
            options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, CoveModelCacheKeyFactory>();
            // Loaded data extensions contribute their own entities/tables to the model at runtime
            // (CoveContext.OnModelCreating calls ext.ConfigureModel), but those are intentionally not
            // part of the core migration snapshot — extensions own their schema. Without this, EF's
            // MigrateAsync validation treats that extension model config as "pending changes" and
            // refuses to apply core migrations. Design-time tooling (migrations add /
            // has-pending-model-changes) still catches missing core migrations, since it builds the
            // model without extensions.
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            // Disable thread safety checks in production for ~5% faster context operations
            options.EnableThreadSafetyChecks(false);
            // Disable detailed errors (only useful for debugging)
            options.EnableDetailedErrors(false);
        });

        // Allow extensions to resolve via DbContext base type
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<CoveContext>());

        services.AddScoped<IVideoRepository, VideoRepository>();
        services.AddScoped<IPerformerRepository, PerformerRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<IStudioRepository, StudioRepository>();
        services.AddScoped<IGalleryRepository, GalleryRepository>();
        services.AddScoped<IImageRepository, ImageRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<ISavedFilterRepository, SavedFilterRepository>();
        services.AddScoped<EmbeddingService>();
        services.AddSingleton<SegmentSpanCacheRegistry>();
        services.AddSingleton<ISegmentSpanCacheInvalidator>(
            sp => sp.GetRequiredService<SegmentSpanCacheRegistry>());
        services.AddScoped<SegmentSpanResolver>();
        services.AddScoped<FacePerformerPropagationService>();
        services.AddScoped<IEmbeddingRepository, EmbeddingRepository>();
        services.AddScoped<ISegmentRepository, SegmentRepository>();
        services.AddScoped<IDetectionRepository, DetectionRepository>();
        services.AddScoped<IFaceRepository, FaceRepository>();
        services.AddScoped<IPerformerMergeService, PerformerMergeService>();
        services.AddScoped<ITagApplicationRepository, TagApplicationRepository>();
        services.AddScoped<IAiRunRepository, AiRunRepository>();
        services.AddScoped<ICustomFieldRepository, CustomFieldRepository>();
        services.AddScoped<IFacePerformerPropagationService>(sp => sp.GetRequiredService<FacePerformerPropagationService>());
        services.AddScoped<IUserEngagementService, UserEngagementService>();
        services.AddScoped<IUserEngagementReadService, UserEngagementReadService>();
        services.AddScoped<IEmbeddingService>(sp => sp.GetRequiredService<EmbeddingService>());
services.AddScoped<ITextEncoderRegistry>(sp => sp.GetRequiredService<EmbeddingService>());
        // Materialized face top-suggestion projection. The list reads the stored Face.TopSuggestion*
        // columns; this service computes/upserts them and services invalidations, and the hosted
        // materializer keeps the backlog drained off the request path. (Replaces the in-memory
        // FaceTopSuggestionCache, which could not scale past its entry cap.)
        services.AddScoped<FaceTopSuggestionService>();
        services.AddScoped<IFaceTopSuggestionMaintenance>(sp => sp.GetRequiredService<FaceTopSuggestionService>());
        services.AddHostedService<FaceTopSuggestionMaterializerService>();

        // Auth / RBAC services
        services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        services.AddSingleton<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<TokenService>();
        services.AddScoped<ITokenService>(provider => provider.GetRequiredService<TokenService>());
        services.AddScoped<IExistingUserPrincipalResolver>(provider => provider.GetRequiredService<TokenService>());
        services.AddScoped<ExternalIdentityService>();
        services.AddScoped<IExternalIdentityService>(provider => provider.GetRequiredService<ExternalIdentityService>());
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IContentRuleService, ContentRuleService>();
        services.AddScoped<IShareLinkService, ShareLinkService>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<IAuditService>(sp => sp.GetRequiredService<AuditService>());
        services.AddHostedService(sp => sp.GetRequiredService<AuditService>());
        // NOTE: BootstrapAuthService is registered explicitly in Program.cs *after*
        // PostgresManagerService so the managed PostgreSQL instance is up before we
        // try to seed permissions/roles/owner. (Hosted services run sequentially in
        // registration order.)

        return services;
    }
}
