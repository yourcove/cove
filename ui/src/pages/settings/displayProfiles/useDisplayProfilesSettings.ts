import { useDeferredValue, useMemo, useRef, useState } from "react";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { videos, segmentDisplayProfiles, segmentLibrary, tags } from "../../../api/client";
import type {
  SegmentDisplayProfile,
  SegmentDisplayProfileCreate,
  SegmentDisplayProfilePreviewRequest,
  SegmentDisplayRule,
  SegmentDisplayRuleCreate,
  TagDetail,
} from "../../../api/types";
import {
  buildDistinctOptions,
  emptyBulkWizardForm,
  emptyProfileForm,
  emptyRuleForm,
  getNextPriority,
  isRulePayloadMeaningful,
  normalizeRulePayloads,
  ruleFormToPayload,
  ruleToPayload,
  rulesSignature,
  type BulkWizardState,
  type ProfileFormState,
  type RuleFormState,
} from "./types";
import { useUnsavedChangesGuard } from "./useUnsavedChangesGuard";
import { compareNatural } from "../../../utils/naturalCompare";
import { formatKindLabel, formatSourceLabel } from "../../segments/segmentDisplayUtils";

// Stable across renders so the list only counts as changed when the query data does.
const noProfiles: SegmentDisplayProfile[] = [];
const noRules: SegmentDisplayRule[] = [];

export const DISCARD_RULE_CHANGES_MESSAGE = "Discard your unsaved rule changes?";

/**
 * Rule edits waiting for Save. Changing a profile's rules rebuilds its stored segments in the background,
 * so the edits are kept here and saved together, which starts that work once. Rules added here carry a
 * negative id until they are saved.
 */
interface RuleDraft {
  profileId: number;
  /** The profile version the edits started from; the save is refused if the rules changed since. */
  baseVersion: number;
  rules: SegmentDisplayRule[];
}

export interface RuleSaveNotice {
  profileId: number;
  kind: "saved" | "unchanged" | "conflict" | "error";
  message?: string;
}

function isConflictError(error: unknown) {
  return error instanceof Error && error.message.startsWith("API Error 409");
}

function isNotFoundError(error: unknown) {
  return error instanceof Error && error.message.startsWith("API Error 404");
}

export function useDisplayProfilesSettings() {
  const queryClient = useQueryClient();
  const [selectedProfileId, setSelectedProfileId] = useState<number | null>(null);
  const [profileModalOpen, setProfileModalOpen] = useState(false);
  const [editingProfile, setEditingProfile] = useState<SegmentDisplayProfile | null>(null);
  const [profileForm, setProfileForm] = useState<ProfileFormState>(emptyProfileForm());
  const [ruleModalOpen, setRuleModalOpen] = useState(false);
  const [editingRule, setEditingRule] = useState<SegmentDisplayRule | null>(null);
  const [ruleForm, setRuleForm] = useState<RuleFormState>(emptyRuleForm());
  const [bulkWizardOpen, setBulkWizardOpen] = useState(false);
  const [bulkWizardForm, setBulkWizardForm] = useState<BulkWizardState>(emptyBulkWizardForm());
  const [previewVideoSearch, setPreviewVideoSearch] = useState("");
  const [previewVideoId, setPreviewVideoId] = useState<number | null>(null);
  const [draft, setDraft] = useState<RuleDraft | null>(null);
  const [saveNotice, setSaveNotice] = useState<RuleSaveNotice | null>(null);
  const nextNewRuleId = useRef(0);

  const { data: profiles = noProfiles, isLoading: profilesLoading } = useQuery({
    queryKey: ["segment-display-profiles"],
    queryFn: () => segmentDisplayProfiles.list(),
  });

  // Re-pick the selection whenever the profile list changes; null first so a cached list is picked at mount.
  const [prevProfiles, setPrevProfiles] = useState<SegmentDisplayProfile[] | null>(null);
  if (profiles !== prevProfiles) {
    setPrevProfiles(profiles);
    if (profiles.length === 0) {
      setSelectedProfileId(null);
    } else {
      setSelectedProfileId((current) =>
        current != null && profiles.some((profile) => profile.id === current)
          ? current
          : (profiles.find((profile) => profile.isDefault)?.id ?? profiles[0].id),
      );
    }
  }

  const selectedProfile = profiles.find((profile) => profile.id === selectedProfileId) ?? null;
  // The rules and the profile version they belong to are read together, the version first: a rule
  // change in between can then only make a save be refused, never let it overwrite rules it did not see.
  const { data: rulesData, isLoading: rulesLoading } = useQuery({
    queryKey: ["segment-display-profiles", selectedProfileId, "rules"],
    queryFn: async () => {
      const profile = await segmentDisplayProfiles.get(selectedProfileId!);
      const rules = await segmentDisplayProfiles.rules.list(selectedProfileId!);
      return { version: profile.version, rules };
    },
    enabled: selectedProfileId != null,
  });
  const savedRules = rulesData?.rules ?? noRules;

  const { data: sourceKeys = [] } = useQuery({
    queryKey: ["segment-library", "distinct-source-keys"],
    queryFn: () => segmentLibrary.distinctSourceKeys(),
    staleTime: 60_000,
  });

  const { data: kinds = [] } = useQuery({
    queryKey: ["segment-library", "distinct-kinds"],
    queryFn: () => segmentLibrary.distinctKinds(),
    staleTime: 60_000,
  });

  const trimmedPreviewVideoSearch = previewVideoSearch.trim();
  const { data: previewVideoResults = [] } = useQuery({
    queryKey: ["display-profiles", "preview-videos", trimmedPreviewVideoSearch],
    queryFn: async () => {
      const response = await videos.find({
        q: trimmedPreviewVideoSearch || undefined,
        perPage: 8,
        sort: "updated_at",
        direction: "desc",
      });
      return response.items;
    },
    enabled: trimmedPreviewVideoSearch.length > 0,
    staleTime: 60_000,
  });

  const { data: previewVideo } = useQuery({
    queryKey: ["display-profiles", "preview-video", previewVideoId],
    queryFn: () => videos.get(previewVideoId!),
    enabled: previewVideoId != null,
    staleTime: 60_000,
  });

  const activeDraft = draft != null && draft.profileId === selectedProfileId ? draft : null;
  const draftRules = activeDraft?.rules ?? savedRules;
  const hasUnsavedChanges = activeDraft != null && rulesSignature(activeDraft.rules) !== rulesSignature(savedRules);
  useUnsavedChangesGuard(hasUnsavedChanges, DISCARD_RULE_CHANGES_MESSAGE);

  const uniqueRuleTagIds = useMemo(
    () => Array.from(new Set(draftRules.map((rule) => rule.tagId).filter((tagId): tagId is number => tagId != null))),
    [draftRules],
  );
  const ruleTagQueries = useQueries({
    queries: uniqueRuleTagIds.map((tagId) => ({
      queryKey: ["display-profiles", "rule-tag", tagId],
      queryFn: () => tags.get(tagId),
      staleTime: 60_000,
    })),
  });
  const ruleTagMap = useMemo(() => {
    const map = new Map<number, TagDetail>();
    uniqueRuleTagIds.forEach((tagId, index) => {
      const detail = ruleTagQueries[index]?.data;
      if (detail) {
        map.set(tagId, detail);
      }
    });
    return map;
  }, [ruleTagQueries, uniqueRuleTagIds]);
  // A rule added from the bulk wizard knows only its tag id; show the tag's name once it has loaded.
  const rules = useMemo(
    () =>
      draftRules.map((rule) => {
        const tagName = rule.tagName ?? (rule.tagId != null ? ruleTagMap.get(rule.tagId)?.name : undefined);
        return tagName === rule.tagName ? rule : { ...rule, tagName };
      }),
    [draftRules, ruleTagMap],
  );

  const selectedRuleTagQuery = useQuery({
    queryKey: ["display-profiles", "selected-rule-tag", ruleForm.tagId],
    queryFn: () => tags.get(ruleForm.tagId!),
    enabled: ruleForm.tagId != null,
    staleTime: 60_000,
  });

  const orderedProfiles = useMemo(
    () =>
      [...profiles].sort(
        (left, right) =>
          Number(right.isDefault) - Number(left.isDefault) ||
          Number(left.userId != null) - Number(right.userId != null) ||
          compareNatural(left.name, right.name),
      ),
    [profiles],
  );

  const sourceKeyOptions = useMemo(
    () => buildDistinctOptions(sourceKeys, ruleForm.sourceKey, formatSourceLabel),
    [ruleForm.sourceKey, sourceKeys],
  );
  const kindOptions = useMemo(
    () => buildDistinctOptions(kinds, ruleForm.kind, formatKindLabel),
    [kinds, ruleForm.kind],
  );

  const persistedPreviewRules = useMemo(
    () => normalizeRulePayloads(savedRules.map((rule) => ruleToPayload(rule))),
    [savedRules],
  );
  const unsavedPreviewRules = useMemo(() => normalizeRulePayloads(rules.map((rule) => ruleToPayload(rule))), [rules]);
  const draftPreviewRules = useMemo(() => {
    if (!ruleModalOpen) {
      return unsavedPreviewRules;
    }

    const draftPayload = ruleFormToPayload(ruleForm, editingRule?.priority ?? getNextPriority(rules));
    if (!isRulePayloadMeaningful(draftPayload)) {
      return unsavedPreviewRules;
    }

    const nextRules = editingRule
      ? rules.map((rule) => (rule.id === editingRule.id ? draftPayload : ruleToPayload(rule)))
      : [draftPayload, ...rules.map((rule) => ruleToPayload(rule))];

    return normalizeRulePayloads(nextRules);
  }, [editingRule, ruleForm, ruleModalOpen, rules, unsavedPreviewRules]);

  const currentPreviewRequest = useMemo<SegmentDisplayProfilePreviewRequest | null>(() => {
    if (previewVideoId == null) {
      return null;
    }

    return {
      videoId: previewVideoId,
      rules: persistedPreviewRules,
    };
  }, [persistedPreviewRules, previewVideoId]);
  const unsavedPreviewRequest = useMemo<SegmentDisplayProfilePreviewRequest | null>(() => {
    if (previewVideoId == null || !hasUnsavedChanges) {
      return null;
    }

    return {
      videoId: previewVideoId,
      rules: unsavedPreviewRules,
    };
  }, [hasUnsavedChanges, previewVideoId, unsavedPreviewRules]);
  const draftPreviewRequest = useMemo<SegmentDisplayProfilePreviewRequest | null>(() => {
    if (previewVideoId == null || !ruleModalOpen) {
      return null;
    }

    return {
      videoId: previewVideoId,
      rules: draftPreviewRules,
    };
  }, [draftPreviewRules, previewVideoId, ruleModalOpen]);

  const deferredCurrentPreviewRequest = useDeferredValue(currentPreviewRequest);
  const deferredUnsavedPreviewRequest = useDeferredValue(unsavedPreviewRequest);
  const deferredDraftPreviewRequest = useDeferredValue(draftPreviewRequest);
  const currentPreviewQuery = useQuery({
    queryKey: [
      "segment-display-profile-preview",
      "current",
      deferredCurrentPreviewRequest ? JSON.stringify(deferredCurrentPreviewRequest) : "none",
    ],
    queryFn: () => segmentDisplayProfiles.preview(deferredCurrentPreviewRequest!),
    enabled: deferredCurrentPreviewRequest != null,
  });
  const unsavedPreviewQuery = useQuery({
    queryKey: [
      "segment-display-profile-preview",
      "unsaved",
      deferredUnsavedPreviewRequest ? JSON.stringify(deferredUnsavedPreviewRequest) : "none",
    ],
    queryFn: () => segmentDisplayProfiles.preview(deferredUnsavedPreviewRequest!),
    enabled: deferredUnsavedPreviewRequest != null,
  });
  const draftPreviewQuery = useQuery({
    queryKey: [
      "segment-display-profile-preview",
      "draft",
      deferredDraftPreviewRequest ? JSON.stringify(deferredDraftPreviewRequest) : "none",
    ],
    queryFn: () => segmentDisplayProfiles.preview(deferredDraftPreviewRequest!),
    enabled: deferredDraftPreviewRequest != null,
  });

  const refreshProfiles = () => {
    queryClient.invalidateQueries({ queryKey: ["segment-display-profiles"] });
    if (selectedProfileId != null) {
      queryClient.invalidateQueries({ queryKey: ["segment-display-profiles", selectedProfileId, "rules"] });
    }
    queryClient.invalidateQueries({ queryKey: ["segment-display-profile-preview"] });
  };

  const createProfileMutation = useMutation({
    mutationFn: (data: SegmentDisplayProfileCreate) => segmentDisplayProfiles.create(data),
    onSuccess: (created) => {
      refreshProfiles();
      discardChanges();
      setSelectedProfileId(created.id);
      setProfileModalOpen(false);
      setEditingProfile(null);
      setProfileForm(emptyProfileForm());
    },
  });
  const updateProfileMutation = useMutation({
    mutationFn: (data: SegmentDisplayProfileCreate) =>
      segmentDisplayProfiles.update(editingProfile!.id, { name: data.name, description: data.description }),
    onSuccess: () => {
      refreshProfiles();
      setProfileModalOpen(false);
      setEditingProfile(null);
      setProfileForm(emptyProfileForm());
    },
  });
  const deleteProfileMutation = useMutation({
    mutationFn: (profileId: number) => segmentDisplayProfiles.delete(profileId),
    onSuccess: () => refreshProfiles(),
  });
  const setDefaultMutation = useMutation({
    mutationFn: (profileId: number) => segmentDisplayProfiles.setDefault(profileId),
    onSuccess: () => refreshProfiles(),
  });
  const saveChangesMutation = useMutation({
    // The panel shows its own message for a refused or failed save.
    meta: { suppressGlobalError: true },
    mutationFn: (pending: RuleDraft) =>
      segmentDisplayProfiles.rules.replace(pending.profileId, {
        expectedVersion: pending.baseVersion,
        rules: pending.rules.map((rule) => ({ ...ruleToPayload(rule), id: rule.id > 0 ? rule.id : undefined })),
      }),
    onSuccess: (saved, pending) => {
      queryClient.setQueryData(["segment-display-profiles", pending.profileId, "rules"], {
        version: saved.profile.version,
        rules: saved.rules,
      });
      queryClient.setQueryData<SegmentDisplayProfile[]>(["segment-display-profiles"], (current) =>
        current?.map((profile) => (profile.id === saved.profile.id ? saved.profile : profile)),
      );
      setDraft(null);
      setSaveNotice({ profileId: pending.profileId, kind: saved.changed ? "saved" : "unchanged" });
      queryClient.invalidateQueries({ queryKey: ["segment-display-profiles"] });
      queryClient.invalidateQueries({ queryKey: ["segment-display-profile-preview"] });
    },
    onError: (error, pending) => {
      if (isConflictError(error) || isNotFoundError(error)) {
        // Load what is stored now, so Discard shows it and the next save starts from it.
        queryClient.invalidateQueries({ queryKey: ["segment-display-profiles"] });
      }
      setSaveNotice({
        profileId: pending.profileId,
        kind: isConflictError(error) ? "conflict" : "error",
        message: error instanceof Error ? error.message : undefined,
      });
    },
  });

  /**
   * Applies an edit to the selected profile's unsaved rules, starting them from the saved ones. Returns
   * false when no edit can be made yet: the rules have not loaded, or a save is in flight.
   */
  const editRules = (update: (current: SegmentDisplayRule[]) => SegmentDisplayRule[]) => {
    if (selectedProfile == null || rulesData == null || saveChangesMutation.isPending) {
      return false;
    }

    setSaveNotice(null);
    setDraft((current) => {
      const base =
        current != null && current.profileId === selectedProfile.id
          ? current
          : { profileId: selectedProfile.id, baseVersion: rulesData.version, rules: rulesData.rules };
      const next = update(base.rules);
      // Edits that end up where the saved rules are leave nothing to save, and no stale base version.
      return rulesSignature(next) === rulesSignature(rulesData.rules) ? null : { ...base, rules: next };
    });
    return true;
  };

  const newRule = (payload: SegmentDisplayRuleCreate, tagName: string | undefined): SegmentDisplayRule => {
    nextNewRuleId.current -= 1;
    return {
      ...payload,
      id: nextNewRuleId.current,
      tagName,
      userId: selectedProfile?.userId,
      createdAt: "",
      updatedAt: "",
    };
  };

  const tagNameFor = (tagId: number | undefined) => {
    if (tagId == null) {
      return undefined;
    }

    const selectedTag = selectedRuleTagQuery.data;
    return selectedTag?.id === tagId ? selectedTag.name : ruleTagMap.get(tagId)?.name;
  };

  const discardChanges = () => {
    setDraft(null);
    setSaveNotice(null);
    queryClient.invalidateQueries({ queryKey: ["segment-display-profiles"] });
  };

  /** Leaving the selected profile drops its unsaved rules, so ask first. */
  const confirmDiscard = () => !hasUnsavedChanges || window.confirm(DISCARD_RULE_CHANGES_MESSAGE);

  const selectProfile = (profileId: number | null) => {
    if (profileId === selectedProfileId) {
      return;
    }
    if (!confirmDiscard()) {
      return;
    }

    discardChanges();
    setSelectedProfileId(profileId);
  };

  const createBulkRules = () => {
    if (saveChangesMutation.isPending) {
      return;
    }

    const currentMaxPriority = Math.max(0, ...rules.map((rule) => rule.priority ?? 0));
    const added = bulkWizardForm.tagIds.map((tagId, index) =>
      newRule(
        {
          sourceKey: undefined,
          kind: undefined,
          tagId,
          hostType: undefined,
          visible: bulkWizardForm.visible,
          minConfidence: bulkWizardForm.minConfidence,
          minDurationSec: bulkWizardForm.minDurationSec,
          mergeGapSec: bulkWizardForm.mergeGapSec,
          collapseToInstant: false,
          colorOverride: bulkWizardForm.useCustomColor ? bulkWizardForm.colorOverride : undefined,
          lane: bulkWizardForm.lane,
          priority: currentMaxPriority + bulkWizardForm.tagIds.length - index,
        },
        ruleTagMap.get(tagId)?.name,
      ),
    );

    if (!editRules((current) => [...added, ...current])) {
      return;
    }

    setBulkWizardOpen(false);
    setBulkWizardForm(emptyBulkWizardForm());
  };

  const saveProfile = () => {
    const payload: SegmentDisplayProfileCreate = {
      name: profileForm.name.trim(),
      description: profileForm.description.trim() || undefined,
      isDefault: profileForm.isDefault,
    };

    if (!payload.name) {
      return;
    }

    if (editingProfile) {
      updateProfileMutation.mutate(payload);
      return;
    }

    createProfileMutation.mutate(payload);
  };

  const saveRule = () => {
    if (selectedProfile == null || saveChangesMutation.isPending) {
      return;
    }

    const payload = ruleFormToPayload(ruleForm, editingRule?.priority ?? getNextPriority(rules));
    if (!isRulePayloadMeaningful(payload)) {
      window.alert("Choose at least one source key, kind, tag, or host type before saving a rule.");
      return;
    }

    const tagName = tagNameFor(payload.tagId);
    if (editingRule) {
      const editedId = editingRule.id;
      // An edit keeps the rule's place, including a priority it never had.
      payload.priority = editingRule.priority;
      // The form has no tag category field, so an edit keeps the rule's own.
      const applied = editRules((current) =>
        current.map((rule) =>
          rule.id === editedId ? { ...rule, ...payload, tagCategory: rule.tagCategory, tagName } : rule,
        ),
      );
      if (!applied) {
        return;
      }
    } else if (!editRules((current) => [newRule(payload, tagName), ...current])) {
      return;
    }

    setRuleModalOpen(false);
    setEditingRule(null);
    setRuleForm(emptyRuleForm());
  };

  return {
    bulkWizardForm,
    bulkWizardOpen,
    currentPreviewQuery,
    deleteProfile: (profileId: number) => deleteProfileMutation.mutate(profileId),
    deleteRule: (ruleId: number) => editRules((current) => current.filter((rule) => rule.id !== ruleId)),
    discardChanges,
    draftPreviewQuery,
    editingProfile,
    editingRule,
    openBulkWizard: () => setBulkWizardOpen(true),
    hasUnsavedChanges,
    /** Rules can be edited once they have loaded and while no save is in flight. */
    rulesEditable: rulesData != null && !saveChangesMutation.isPending,
    openCreateProfile: () => {
      if (!confirmDiscard()) {
        return;
      }

      setEditingProfile(null);
      setProfileForm(emptyProfileForm());
      setProfileModalOpen(true);
    },
    openCreateRule: () => {
      setEditingRule(null);
      setRuleForm(emptyRuleForm());
      setRuleModalOpen(true);
    },
    openEditProfile: (profile: SegmentDisplayProfile) => {
      setEditingProfile(profile);
      setProfileForm({
        name: profile.name,
        description: profile.description ?? "",
        isDefault: profile.isDefault,
      });
      setProfileModalOpen(true);
    },
    openEditRule: (rule: SegmentDisplayRule) => {
      setEditingRule(rule);
      setRuleForm({
        sourceKey: rule.sourceKey ?? "",
        kind: rule.kind ?? "",
        tagId: rule.tagId,
        hostType: rule.hostType ?? "",
        visible: rule.visible,
        minConfidence: rule.minConfidence,
        minDurationSec: rule.minDurationSec,
        mergeGapSec: rule.mergeGapSec,
        collapseToInstant: rule.collapseToInstant,
        useCustomColor: !!rule.colorOverride,
        colorOverride: rule.colorOverride ?? "#3b82f6",
        lane: rule.lane,
      });
      setRuleModalOpen(true);
    },
    orderedProfiles,
    overrideRuleCount: Array.from(ruleTagMap.values()).filter((tag) => tag.showAsSegment != null).length,
    previewVideo,
    previewVideoResults,
    previewVideoSearch,
    profileForm,
    profileModalOpen,
    profileSavePending: createProfileMutation.isPending || updateProfileMutation.isPending,
    profilesLoading,
    refreshProfiles,
    reorderRules: (nextRules: SegmentDisplayRule[]) =>
      editRules(() => nextRules.map((rule, index) => ({ ...rule, priority: nextRules.length - index }))),
    ruleForm,
    ruleModalOpen,
    rules,
    rulesLoading,
    ruleTagMap,
    saveChanges: () => {
      if (activeDraft != null && hasUnsavedChanges) {
        saveChangesMutation.mutate(activeDraft);
      }
    },
    saveChangesPending: saveChangesMutation.isPending,
    saveNotice: saveNotice != null && saveNotice.profileId === selectedProfileId ? saveNotice : null,
    saveProfile,
    saveRule,
    selectedProfile,
    selectedProfileId,
    selectedRuleTag: selectedRuleTagQuery.data,
    setBulkWizardForm,
    setDefaultProfile: (profileId: number) => setDefaultMutation.mutate(profileId),
    setPreviewVideoId,
    setPreviewVideoSearch,
    setProfileForm,
    setRuleForm,
    setSelectedProfileId: selectProfile,
    sourceKeyOptions,
    unsavedPreviewQuery,
    kindOptions,
    closeBulkWizard: () => setBulkWizardOpen(false),
    closeProfileModal: () => setProfileModalOpen(false),
    closeRuleModal: () => setRuleModalOpen(false),
    createBulkRules,
  };
}
