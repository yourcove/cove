import { useDeferredValue, useEffect, useMemo, useState } from "react";
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
  type BulkWizardState,
  type ProfileFormState,
  type RuleFormState,
} from "./types";
import { bulkCreateDisplayProfileRules } from "./bulkCreateRules";

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

  const { data: profiles = [], isLoading: profilesLoading } = useQuery({
    queryKey: ["segment-display-profiles"],
    queryFn: () => segmentDisplayProfiles.list(),
  });

  useEffect(() => {
    if (profiles.length === 0) {
      setSelectedProfileId(null);
      return;
    }

    setSelectedProfileId((current) =>
      current != null && profiles.some((profile) => profile.id === current)
        ? current
        : (profiles.find((profile) => profile.isDefault)?.id ?? profiles[0].id),
    );
  }, [profiles]);

  const selectedProfile = profiles.find((profile) => profile.id === selectedProfileId) ?? null;
  const { data: rules = [], isLoading: rulesLoading } = useQuery({
    queryKey: ["segment-display-profiles", selectedProfileId, "rules"],
    queryFn: () => segmentDisplayProfiles.rules.list(selectedProfileId!),
    enabled: selectedProfileId != null,
  });

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

  const uniqueRuleTagIds = useMemo(
    () => Array.from(new Set(rules.map((rule) => rule.tagId).filter((tagId): tagId is number => tagId != null))),
    [rules],
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
          left.name.localeCompare(right.name),
      ),
    [profiles],
  );

  const sourceKeyOptions = useMemo(
    () => buildDistinctOptions(sourceKeys, ruleForm.sourceKey),
    [ruleForm.sourceKey, sourceKeys],
  );
  const kindOptions = useMemo(() => buildDistinctOptions(kinds, ruleForm.kind), [kinds, ruleForm.kind]);

  const persistedPreviewRules = useMemo(() => normalizeRulePayloads(rules.map((rule) => ruleToPayload(rule))), [rules]);
  const draftPreviewRules = useMemo(() => {
    if (!ruleModalOpen) {
      return persistedPreviewRules;
    }

    const draftPayload = ruleFormToPayload(ruleForm, editingRule?.priority ?? getNextPriority(rules));
    if (!isRulePayloadMeaningful(draftPayload)) {
      return persistedPreviewRules;
    }

    const nextRules = editingRule
      ? rules.map((rule) => (rule.id === editingRule.id ? draftPayload : ruleToPayload(rule)))
      : [draftPayload, ...rules.map((rule) => ruleToPayload(rule))];

    return normalizeRulePayloads(nextRules);
  }, [editingRule, persistedPreviewRules, ruleForm, ruleModalOpen, rules]);

  const currentPreviewRequest = useMemo<SegmentDisplayProfilePreviewRequest | null>(() => {
    if (previewVideoId == null) {
      return null;
    }

    return {
      videoId: previewVideoId,
      rules: persistedPreviewRules,
    };
  }, [persistedPreviewRules, previewVideoId]);
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
  const createRuleMutation = useMutation({
    mutationFn: (data: SegmentDisplayRuleCreate) => segmentDisplayProfiles.rules.create(selectedProfileId!, data),
    onSuccess: () => {
      refreshProfiles();
      setRuleModalOpen(false);
      setEditingRule(null);
      setRuleForm(emptyRuleForm());
    },
  });
  const updateRuleMutation = useMutation({
    mutationFn: (data: SegmentDisplayRuleCreate) =>
      segmentDisplayProfiles.rules.update(selectedProfileId!, editingRule!.id, data),
    onSuccess: () => {
      refreshProfiles();
      setRuleModalOpen(false);
      setEditingRule(null);
      setRuleForm(emptyRuleForm());
    },
  });
  const deleteRuleMutation = useMutation({
    mutationFn: (ruleId: number) => segmentDisplayProfiles.rules.delete(selectedProfileId!, ruleId),
    onSuccess: () => refreshProfiles(),
  });
  const reorderRulesMutation = useMutation({
    mutationFn: async (nextRules: SegmentDisplayRule[]) => {
      if (selectedProfileId == null) {
        return;
      }

      const total = nextRules.length;
      const updates = nextRules
        .map((rule, index) => ({ rule, nextPriority: total - index }))
        .filter(({ rule, nextPriority }) => (rule.priority ?? 0) !== nextPriority)
        .map(({ rule, nextPriority }) =>
          segmentDisplayProfiles.rules.update(selectedProfileId, rule.id, {
            ...ruleToPayload(rule),
            priority: nextPriority,
          }),
        );

      await Promise.all(updates);
    },
    onSuccess: () => refreshProfiles(),
  });
  const bulkCreateMutation = useMutation({
    mutationFn: async () => {
      if (selectedProfileId == null) {
        return;
      }

      const currentMaxPriority = Math.max(0, ...rules.map((rule) => rule.priority ?? 0));
      const payloads = bulkWizardForm.tagIds.map(
        (tagId, index) =>
          ({
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
          }) satisfies SegmentDisplayRuleCreate,
      );

      await bulkCreateDisplayProfileRules(selectedProfileId, payloads);
    },
    onSuccess: () => {
      refreshProfiles();
      setBulkWizardOpen(false);
      setBulkWizardForm(emptyBulkWizardForm());
    },
  });

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
    if (selectedProfileId == null) {
      return;
    }

    const payload = ruleFormToPayload(ruleForm, editingRule?.priority ?? getNextPriority(rules));
    if (!isRulePayloadMeaningful(payload)) {
      window.alert("Choose at least one source key, kind, tag, or host type before saving a rule.");
      return;
    }

    if (editingRule) {
      updateRuleMutation.mutate(payload);
      return;
    }

    createRuleMutation.mutate(payload);
  };

  return {
    bulkWizardForm,
    bulkWizardOpen,
    currentPreviewQuery,
    deleteProfile: (profileId: number) => deleteProfileMutation.mutate(profileId),
    deleteRule: (ruleId: number) => deleteRuleMutation.mutate(ruleId),
    draftPreviewQuery,
    editingProfile,
    editingRule,
    openBulkWizard: () => setBulkWizardOpen(true),
    openCreateProfile: () => {
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
    reorderRules: (nextRules: SegmentDisplayRule[]) => reorderRulesMutation.mutate(nextRules),
    reorderRulesPending: reorderRulesMutation.isPending,
    ruleForm,
    ruleModalOpen,
    ruleSavePending: createRuleMutation.isPending || updateRuleMutation.isPending,
    rules,
    rulesLoading,
    ruleTagMap,
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
    setSelectedProfileId,
    sourceKeyOptions,
    kindOptions,
    closeBulkWizard: () => setBulkWizardOpen(false),
    closeProfileModal: () => setProfileModalOpen(false),
    closeRuleModal: () => setRuleModalOpen(false),
    createBulkRules: () => bulkCreateMutation.mutate(),
    bulkSavePending: bulkCreateMutation.isPending,
  };
}
