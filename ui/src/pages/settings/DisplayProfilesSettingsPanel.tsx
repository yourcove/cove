import { GripVertical, Pencil, Plus, RefreshCw, Sparkles, Star, Trash2 } from "lucide-react";
import { SortableList } from "../../components/SortableList";
import { buildRouteUrl, navigateToUrl } from "../../router/location";
import { BulkFromTagsWizard } from "./displayProfiles/BulkFromTagsWizard";
import { ProfileFormModal } from "./displayProfiles/ProfileFormModal";
import { RuleFormModal } from "./displayProfiles/RuleFormModal";
import { RulesPreviewPane } from "./displayProfiles/RulesPreviewPane";
import { useDisplayProfilesSettings } from "./displayProfiles/useDisplayProfilesSettings";
import { formatRuleTitle } from "./displayProfiles/types";

interface Props {
  canWrite: boolean;
}

export function DisplayProfilesSettingsPanel({ canWrite }: Props) {
  const state = useDisplayProfilesSettings();
  const selectedProfile = state.selectedProfile;

  return (
    <div className="space-y-6">
      <ProfileFormModal
        open={state.profileModalOpen}
        editingProfile={state.editingProfile}
        form={state.profileForm}
        setForm={state.setProfileForm}
        onClose={state.closeProfileModal}
        onSave={state.saveProfile}
        saving={state.profileSavePending}
      />

      <RuleFormModal
        open={state.ruleModalOpen}
        editingRule={state.editingRule}
        form={state.ruleForm}
        setForm={state.setRuleForm}
        sourceKeyOptions={state.sourceKeyOptions}
        kindOptions={state.kindOptions}
        selectedRuleTag={state.selectedRuleTag}
        previewPane={
          <RulesPreviewPane
            title="Rule Preview"
            description="Compare the saved profile against this draft before saving."
            previewVideo={state.previewVideo}
            cards={[
              {
                title: "Current profile",
                spans: state.currentPreviewQuery.data?.spans ?? [],
                loading: state.currentPreviewQuery.isLoading || state.currentPreviewQuery.isFetching,
              },
              {
                title: state.editingRule ? "Draft update" : "Draft rule",
                spans: state.draftPreviewQuery.data?.spans ?? [],
                loading: state.draftPreviewQuery.isLoading || state.draftPreviewQuery.isFetching,
              },
            ]}
            emptyMessage="Pick a preview video in the panel to compare the current rules against this draft before saving."
          />
        }
        onClose={state.closeRuleModal}
        onSave={state.saveRule}
        onOpenTagDetail={openTagDetail}
        saving={state.ruleSavePending}
      />

      <BulkFromTagsWizard
        open={state.bulkWizardOpen}
        form={state.bulkWizardForm}
        setForm={state.setBulkWizardForm}
        onClose={state.closeBulkWizard}
        onSave={() => {
          if (state.bulkWizardForm.tagIds.length === 0) {
            window.alert("Choose at least one tag for the bulk rule generator.");
            return;
          }

          state.createBulkRules();
        }}
        saving={state.bulkSavePending}
      />

      <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border bg-card p-4">
        <div>
          <h3 className="text-lg font-semibold text-foreground">Display Profiles</h3>
          <p className="mt-1 text-sm text-secondary">
            Edit rules against real source keys and kinds, preview them on a video, and drag rows to change precedence.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => state.refreshProfiles()}
            className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
          >
            <RefreshCw className="h-4 w-4" />
            Refresh
          </button>
          {canWrite ? (
            <button
              type="button"
              onClick={state.openCreateProfile}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover"
            >
              <Plus className="h-4 w-4" />
              New profile
            </button>
          ) : null}
        </div>
      </div>

      <div className="grid gap-6 lg:grid-cols-[300px,minmax(0,1fr)]">
        <div className="space-y-3">
          {state.profilesLoading ? (
            <div className="rounded-xl border border-border bg-card p-4 text-sm text-secondary">
              Loading profiles...
            </div>
          ) : state.orderedProfiles.length === 0 ? (
            <div className="rounded-xl border border-dashed border-border bg-card p-4 text-sm text-secondary">
              No display profiles are available yet.
            </div>
          ) : (
            state.orderedProfiles.map((profile) => {
              const selected = profile.id === state.selectedProfileId;
              return (
                <button
                  key={profile.id}
                  type="button"
                  onClick={() => state.setSelectedProfileId(profile.id)}
                  className={`w-full rounded-xl border px-4 py-3 text-left transition-colors ${selected ? "border-accent bg-accent/10" : "border-border bg-card/70 hover:border-accent"}`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-sm font-medium text-foreground">{profile.name}</span>
                    {profile.isDefault ? <Star className="h-4 w-4 text-accent" /> : null}
                  </div>
                  <div className="mt-2 flex flex-wrap gap-2 text-xs text-secondary">
                    <span>{profile.userId == null ? "Global" : "Mine"}</span>
                    <span>v{profile.version}</span>
                    {profile.isSystem ? <span>System</span> : null}
                  </div>
                </button>
              );
            })
          )}
        </div>

        <div className="space-y-4">
          {selectedProfile ? (
            <div className="rounded-xl border border-border bg-card p-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h4 className="text-lg font-semibold text-foreground">{selectedProfile.name}</h4>
                  <p className="mt-1 text-sm text-secondary">{selectedProfile.description || "No description set."}</p>
                  {state.overrideRuleCount > 0 ? (
                    <div className="mt-3 inline-flex items-center gap-2 rounded-full bg-amber-500/10 px-3 py-1 text-xs text-amber-100">
                      <Sparkles className="h-3.5 w-3.5" />
                      {state.overrideRuleCount} rule{state.overrideRuleCount === 1 ? "" : "s"} target tag
                      {state.overrideRuleCount === 1 ? "" : "s"} with a global player-bar override
                    </div>
                  ) : null}
                </div>
                {canWrite ? (
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => state.openEditProfile(selectedProfile)}
                      className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                    >
                      <Pencil className="h-4 w-4" />
                      Edit
                    </button>
                    {!selectedProfile.isDefault ? (
                      <button
                        type="button"
                        onClick={() => state.setDefaultProfile(selectedProfile.id)}
                        className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                      >
                        <Star className="h-4 w-4" />
                        Make default
                      </button>
                    ) : null}
                    {!selectedProfile.isSystem ? (
                      <button
                        type="button"
                        onClick={() => {
                          if (window.confirm(`Delete profile \"${selectedProfile.name}\"?`)) {
                            state.deleteProfile(selectedProfile.id);
                          }
                        }}
                        className="inline-flex items-center gap-2 rounded-lg border border-red-400/30 px-3 py-2 text-sm text-red-200 transition-colors hover:border-red-400"
                      >
                        <Trash2 className="h-4 w-4" />
                        Delete
                      </button>
                    ) : null}
                  </div>
                ) : null}
              </div>
            </div>
          ) : null}

          <RulesPreviewPane
            title="Preview Video"
            description="Pick a video to compare the saved profile against a draft rule before saving."
            previewVideo={state.previewVideo}
            previewVideoSearch={state.previewVideoSearch}
            onPreviewVideoSearchChange={state.setPreviewVideoSearch}
            previewVideoResults={state.previewVideoResults}
            onSelectPreviewVideo={(videoId) => {
              state.setPreviewVideoId(videoId);
              state.setPreviewVideoSearch("");
            }}
            cards={[
              {
                title: "Current profile preview",
                spans: state.currentPreviewQuery.data?.spans ?? [],
                loading: state.currentPreviewQuery.isLoading || state.currentPreviewQuery.isFetching,
              },
            ]}
            emptyMessage="Select a video to enable live preview."
          />

          <div className="rounded-xl border border-border bg-card p-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <h4 className="text-sm font-semibold uppercase tracking-wide text-muted">Rules</h4>
                <p className="mt-1 text-sm text-secondary">
                  Drag rows to change precedence. Higher rows win when specificity ties.
                </p>
              </div>
              {canWrite && selectedProfile ? (
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={state.openBulkWizard}
                    className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                  >
                    <Sparkles className="h-4 w-4" />
                    Bulk from tags
                  </button>
                  <button
                    type="button"
                    onClick={state.openCreateRule}
                    className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                  >
                    <Plus className="h-4 w-4" />
                    Add rule
                  </button>
                </div>
              ) : null}
            </div>

            {state.rulesLoading ? (
              <div className="mt-4 text-sm text-secondary">Loading rules...</div>
            ) : state.rules.length === 0 ? (
              <div className="mt-4 rounded-xl border border-dashed border-border bg-surface/40 px-4 py-6 text-sm text-secondary">
                No rules defined for this profile yet.
              </div>
            ) : (
              <SortableList
                items={state.rules}
                getKey={(rule) => rule.id}
                onReorder={state.reorderRules}
                disabled={!canWrite || state.reorderRulesPending}
                className="mt-4 space-y-2"
                renderItem={(rule, { dragHandleProps, isDragging, isOver }) => {
                  const tagOverride = rule.tagId != null ? state.ruleTagMap.get(rule.tagId) : undefined;
                  return (
                    <div
                      className={`rounded-xl border p-4 transition-colors ${isDragging ? "border-accent opacity-50" : isOver ? "border-accent bg-accent/5" : "border-border bg-surface/40"}`}
                    >
                      <div className="flex flex-wrap items-start justify-between gap-3">
                        <div className="flex min-w-0 items-start gap-3">
                          {canWrite ? (
                            <span
                              {...dragHandleProps}
                              className="mt-0.5 inline-flex shrink-0 cursor-grab items-center text-muted active:cursor-grabbing"
                            >
                              <GripVertical className="h-4 w-4" />
                            </span>
                          ) : null}
                          <div className="min-w-0">
                            <div className="text-sm font-medium text-foreground">{formatRuleTitle(rule)}</div>
                            <div className="mt-2 flex flex-wrap gap-2 text-xs text-secondary">
                              <span
                                className={`rounded-full px-2 py-1 ${rule.visible ? "bg-emerald-500/10 text-emerald-300" : "bg-red-500/10 text-red-300"}`}
                              >
                                {rule.visible ? "Visible" : "Hidden"}
                              </span>
                              {rule.sourceKey ? (
                                <span className="rounded-full bg-surface px-2 py-1">{rule.sourceKey}</span>
                              ) : null}
                              {rule.kind ? (
                                <span className="rounded-full bg-surface px-2 py-1">{rule.kind}</span>
                              ) : null}
                              {rule.tagName ? (
                                <span className="rounded-full bg-surface px-2 py-1">{rule.tagName}</span>
                              ) : null}
                              {rule.minConfidence != null ? (
                                <span className="rounded-full bg-surface px-2 py-1">
                                  Min conf. {rule.minConfidence}
                                </span>
                              ) : null}
                              {rule.minDurationSec != null ? (
                                <span className="rounded-full bg-surface px-2 py-1">
                                  Min dur. {rule.minDurationSec}s
                                </span>
                              ) : null}
                              {rule.mergeGapSec != null ? (
                                <span className="rounded-full bg-surface px-2 py-1">Merge {rule.mergeGapSec}s</span>
                              ) : null}
                              {rule.lane != null ? (
                                <span className="rounded-full bg-surface px-2 py-1">Lane {rule.lane}</span>
                              ) : null}
                              {tagOverride?.showAsSegment != null ? (
                                <button
                                  type="button"
                                  onClick={() => openTagDetail(tagOverride.id)}
                                  className="rounded-full bg-amber-500/10 px-2 py-1 text-amber-100 transition-colors hover:bg-amber-500/20"
                                >
                                  Tag override active
                                </button>
                              ) : null}
                            </div>
                          </div>
                        </div>
                        {canWrite ? (
                          <div className="flex flex-wrap gap-2">
                            <button
                              type="button"
                              onClick={() => state.openEditRule(rule)}
                              className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                            >
                              <Pencil className="h-4 w-4" />
                              Edit
                            </button>
                            <button
                              type="button"
                              onClick={() => {
                                if (window.confirm(`Delete rule #${rule.id}?`)) {
                                  state.deleteRule(rule.id);
                                }
                              }}
                              className="inline-flex items-center gap-2 rounded-lg border border-red-400/30 px-3 py-2 text-sm text-red-200 transition-colors hover:border-red-400"
                            >
                              <Trash2 className="h-4 w-4" />
                              Delete
                            </button>
                          </div>
                        ) : null}
                      </div>
                    </div>
                  );
                }}
              />
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function openTagDetail(tagId: number) {
  const route = { page: "tag", id: tagId } as const;
  navigateToUrl(buildRouteUrl(route), { state: route });
}
