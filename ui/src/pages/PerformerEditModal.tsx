import { useState, useEffect, useMemo } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { performers, tags as tagsApi } from "../api/client";
import type { Performer, PerformerUpdate } from "../api/types";
import { EditModal, Field, TextInput, TextArea, NumberInput, SaveButton } from "../components/EditModal";
import { InteractiveRatingField } from "../components/Rating";
import { CustomFieldsEditor, buildTagProvenanceById } from "../components/shared";
import { StringListEditor } from "../components/StringListEditor";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { SelectedTagChips, type SelectableTag } from "../components/TagSelector";
import { useAutocomplete, type AutocompleteItem } from "../hooks/useAutocomplete";

interface Props {
  performer: Performer;
  open: boolean;
  onClose: () => void;
}

export const GENDER_OPTIONS = [
  { value: "Male", label: "Male" },
  { value: "Female", label: "Female" },
  { value: "TransMale", label: "Trans Male" },
  { value: "TransFemale", label: "Trans Female" },
  { value: "Intersex", label: "Intersex" },
  { value: "NonBinary", label: "Non-Binary" },
];

export const CIRCUMCISED_OPTIONS = [
  { value: "Cut", label: "Cut" },
  { value: "Uncut", label: "Uncut" },
];

type SelectedTagOption = SelectableTag;
type TagAutocompleteValue =
  | { kind: "tag"; tag: SelectedTagOption }
  | { kind: "create"; query: string };

function buildSelectedTagLookup(tags: Performer["tags"]): Record<number, SelectedTagOption> {
  return Object.fromEntries(tags.map((tag) => [tag.id, tag])) as Record<number, SelectedTagOption>;
}

export function PerformerEditModal({ performer, open, onClose }: Props) {
  const queryClient = useQueryClient();

  const [name, setName] = useState(performer.name);
  const [disambiguation, setDisambiguation] = useState(performer.disambiguation || "");
  const [gender, setGender] = useState(performer.gender || "");
  const [birthdate, setBirthdate] = useState(performer.birthdate || "");
  const [ethnicity, setEthnicity] = useState(performer.ethnicity || "");
  const [country, setCountry] = useState(performer.country || "");
  const [eyeColor, setEyeColor] = useState(performer.eyeColor || "");
  const [hairColor, setHairColor] = useState(performer.hairColor || "");
  const [heightCm, setHeightCm] = useState<number | undefined>(performer.heightCm ?? undefined);
  const [weight, setWeight] = useState<number | undefined>(performer.weight ?? undefined);
  const [measurements, setMeasurements] = useState(performer.measurements || "");
  const [tattoos, setTattoos] = useState(performer.tattoos || "");
  const [piercings, setPiercings] = useState(performer.piercings || "");
  const [rating, setRating] = useState<number | undefined>(undefined);
  const [details, setDetails] = useState(performer.details || "");
  const [deathDate, setDeathDate] = useState(performer.deathDate || "");
  const [fakeTits, setFakeTits] = useState(performer.fakeTits || "");
  const [penisLength, setPenisLength] = useState<number | undefined>(performer.penisLength ?? undefined);
  const [circumcised, setCircumcised] = useState(performer.circumcised || "");
  const [careerStart, setCareerStart] = useState(performer.careerStart || "");
  const [careerEnd, setCareerEnd] = useState(performer.careerEnd || "");
  const [urls, setUrls] = useState(performer.urls.length > 0 ? performer.urls : [""]);
  const [aliases, setAliases] = useState(performer.aliases.length > 0 ? performer.aliases : [""]);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(performer.tags.map((t) => t.id));
  const [selectedTagsById, setSelectedTagsById] = useState<Record<number, SelectedTagOption>>(() => buildSelectedTagLookup(performer.tags));
  const [tagSearch, setTagSearch] = useState("");
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...(performer.customFields ?? {}) });
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(performer.remoteIds.map((remoteId) => ({ ...remoteId })));
  const trimmedTagSearch = tagSearch.trim();

  const { data: tagResults, isLoading: tagResultsLoading } = useQuery({
    queryKey: ["performer-tags-search", trimmedTagSearch],
    queryFn: () => tagsApi.find({ q: trimmedTagSearch, perPage: 20, sort: "name", direction: "asc" }),
    enabled: trimmedTagSearch.length > 0,
    staleTime: 60000,
  });

  useEffect(() => {
    setName(performer.name);
    setDisambiguation(performer.disambiguation || "");
    setGender(performer.gender || "");
    setBirthdate(performer.birthdate || "");
    setEthnicity(performer.ethnicity || "");
    setCountry(performer.country || "");
    setEyeColor(performer.eyeColor || "");
    setHairColor(performer.hairColor || "");
    setHeightCm(performer.heightCm ?? undefined);
    setWeight(performer.weight ?? undefined);
    setMeasurements(performer.measurements || "");
    setTattoos(performer.tattoos || "");
    setPiercings(performer.piercings || "");
    setRating(undefined);
    setDetails(performer.details || "");
    setDeathDate(performer.deathDate || "");
    setFakeTits(performer.fakeTits || "");
    setPenisLength(performer.penisLength ?? undefined);
    setCircumcised(performer.circumcised || "");
    setCareerStart(performer.careerStart || "");
    setCareerEnd(performer.careerEnd || "");
    setUrls(performer.urls.length > 0 ? performer.urls : [""]);
    setAliases(performer.aliases.length > 0 ? performer.aliases : [""]);
    setSelectedTagIds(performer.tags.map((t) => t.id));
    setSelectedTagsById(buildSelectedTagLookup(performer.tags));
    setTagSearch("");
    setCustomFields({ ...(performer.customFields ?? {}) });
    setRemoteIds(performer.remoteIds.map((remoteId) => ({ ...remoteId })));
  }, [performer]);

  const mutation = useMutation({
    mutationFn: (data: PerformerUpdate) => performers.update(performer.id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["performer", performer.id] });
      queryClient.invalidateQueries({ queryKey: ["performers"] });
      onClose();
    },
  });

  const handleSave = () => {
    const urlList = urls.map((url) => url.trim()).filter(Boolean);
    const aliasList = aliases.map((alias) => alias.trim()).filter(Boolean);
    mutation.mutate({
      name,
      disambiguation: disambiguation || undefined,
      gender: gender || undefined,
      birthdate: birthdate || undefined,
      ethnicity: ethnicity || undefined,
      country: country || undefined,
      eyeColor: eyeColor || undefined,
      hairColor: hairColor || undefined,
      heightCm,
      weight,
      measurements: measurements || undefined,
      tattoos: tattoos || undefined,
      piercings: piercings || undefined,
      deathDate: deathDate || undefined,
      fakeTits: fakeTits || undefined,
      penisLength,
      circumcised: circumcised || undefined,
      careerStart: careerStart || undefined,
      careerEnd: careerEnd || undefined,
      rating,
      details: details || undefined,
      urls: urlList,
      aliases: aliasList,
      tagIds: selectedTagIds,
      customFields,
      remoteIds: normalizeRemoteIds(remoteIds),
    });
  };

  const filteredTags = tagResults?.items.filter((tag) => !selectedTagIds.includes(tag.id)) ?? [];
  const tagExactMatchExists = useMemo(
    () => trimmedTagSearch && tagResults?.items.some((tag) => tag.name.toLowerCase() === trimmedTagSearch.toLowerCase()),
    [tagResults?.items, trimmedTagSearch],
  );
  const addTag = (tag: SelectedTagOption) => {
    setSelectedTagIds((current) => current.includes(tag.id) ? current : [...current, tag.id]);
    setSelectedTagsById((current) => ({ ...current, [tag.id]: tag }));
    setTagSearch("");
  };
  const tagCreateMutation = useMutation({
    mutationFn: async (name: string) => tagsApi.create({ name }),
    onSuccess: (result) => {
      addTag(result);
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });
  const showTagCreateOption = trimmedTagSearch && !tagResultsLoading && !tagExactMatchExists;
  const tagAutocompleteItems = useMemo<AutocompleteItem<TagAutocompleteValue>[]>(() => {
    const items: AutocompleteItem<TagAutocompleteValue>[] = filteredTags.map((tag) => ({
      key: `tag:${tag.id}`,
      value: { kind: "tag", tag },
    }));
    if (showTagCreateOption) {
      items.push({
        key: `create:${trimmedTagSearch.toLowerCase()}`,
        value: { kind: "create", query: trimmedTagSearch },
        disabled: tagCreateMutation.isPending,
      });
    }
    return items;
  }, [filteredTags, showTagCreateOption, tagCreateMutation.isPending, trimmedTagSearch]);
  const tagAutocomplete = useAutocomplete({
    items: tagAutocompleteItems,
    inputValue: tagSearch,
    onInputValueChange: setTagSearch,
    onSelect: (item) => {
      if (item.kind === "create") {
        tagCreateMutation.mutate(item.query);
        return false;
      }
      addTag(item.tag);
    },
  });
  const selectedTags = selectedTagIds
    .map((tagId) => selectedTagsById[tagId])
    .filter((tag): tag is SelectedTagOption => Boolean(tag));
  const tagProvenanceById = buildTagProvenanceById(performer.tags, performer.fieldProvenance);

  return (
    <EditModal title="Edit Performer" open={open} onClose={onClose}>
      <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4">
        <Field label="Name *" fieldProvenance={performer.fieldProvenance} fieldKey="name">
          <TextInput value={name} onChange={setName} placeholder="Performer name" />
        </Field>
        <Field label="Disambiguation" fieldProvenance={performer.fieldProvenance} fieldKey="disambiguation">
          <TextInput value={disambiguation} onChange={setDisambiguation} placeholder="e.g. (2020s)" />
        </Field>
      </div>

      <div className="grid grid-cols-4 gap-4">
        <Field label="Gender" fieldProvenance={performer.fieldProvenance} fieldKey="gender">
          <select
            value={gender}
            onChange={(e) => setGender(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          >
            <option value="">—</option>
            {GENDER_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
        </Field>
        <Field label="Birthdate" fieldProvenance={performer.fieldProvenance} fieldKey="birthdate">
          <input
            type="date"
            value={birthdate}
            onChange={(e) => setBirthdate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
        <Field label="Death Date" fieldProvenance={performer.fieldProvenance} fieldKey="deathDate">
          <input
            type="date"
            value={deathDate}
            onChange={(e) => setDeathDate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
        <Field label="Country" fieldProvenance={performer.fieldProvenance} fieldKey="country">
          <TextInput value={country} onChange={setCountry} placeholder="e.g. US" />
        </Field>
      </div>

      <div className="grid grid-cols-3 gap-4">
        <Field label="Ethnicity" fieldProvenance={performer.fieldProvenance} fieldKey="ethnicity">
          <TextInput value={ethnicity} onChange={setEthnicity} />
        </Field>
        <Field label="Eye Color" fieldProvenance={performer.fieldProvenance} fieldKey="eyeColor">
          <TextInput value={eyeColor} onChange={setEyeColor} />
        </Field>
        <Field label="Hair Color" fieldProvenance={performer.fieldProvenance} fieldKey="hairColor">
          <TextInput value={hairColor} onChange={setHairColor} />
        </Field>
      </div>

      <div className="grid grid-cols-3 gap-4">
        <Field label="Height (cm)" fieldProvenance={performer.fieldProvenance} fieldKey="heightCm">
          <NumberInput value={heightCm} onChange={setHeightCm} min={50} max={250} />
        </Field>
        <Field label="Weight (kg)" fieldProvenance={performer.fieldProvenance} fieldKey="weight">
          <NumberInput value={weight} onChange={setWeight} min={20} max={300} />
        </Field>
        <Field label="Measurements" fieldProvenance={performer.fieldProvenance} fieldKey="measurements">
          <TextInput value={measurements} onChange={setMeasurements} placeholder="34D-24-34" />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Tattoos" fieldProvenance={performer.fieldProvenance} fieldKey="tattoos">
          <TextInput value={tattoos} onChange={setTattoos} />
        </Field>
        <Field label="Piercings" fieldProvenance={performer.fieldProvenance} fieldKey="piercings">
          <TextInput value={piercings} onChange={setPiercings} />
        </Field>
      </div>

      <div className="grid grid-cols-3 gap-4">
        <Field label="Fake Tits" fieldProvenance={performer.fieldProvenance} fieldKey="fakeTits">
          <TextInput value={fakeTits} onChange={setFakeTits} placeholder="e.g. Augmented" />
        </Field>
        <Field label="Penis Length (cm)" fieldProvenance={performer.fieldProvenance} fieldKey="penisLength">
          <NumberInput value={penisLength} onChange={setPenisLength} min={0} max={50} />
        </Field>
        <Field label="Circumcised" fieldProvenance={performer.fieldProvenance} fieldKey="circumcised">
          <select
            value={circumcised}
            onChange={(e) => setCircumcised(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          >
            <option value="">—</option>
            {CIRCUMCISED_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Career Start" fieldProvenance={performer.fieldProvenance} fieldKey="careerStart">
          <input
            type="date"
            value={careerStart}
            onChange={(e) => setCareerStart(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
        <Field label="Career End" fieldProvenance={performer.fieldProvenance} fieldKey="careerEnd">
          <input
            type="date"
            value={careerEnd}
            onChange={(e) => setCareerEnd(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <Field label="Details" fieldProvenance={performer.fieldProvenance} fieldKey="details">
        <TextArea value={details} onChange={setDetails} placeholder="Bio / notes" rows={2} />
      </Field>

      <div className="grid grid-cols-2 gap-4">
        <InteractiveRatingField value={rating} onChange={setRating} label="Rating" fieldProvenance={performer.fieldProvenance} />
        <Field label="Aliases" fieldProvenance={performer.fieldProvenance} fieldKey="aliases">
          <StringListEditor values={aliases} onChange={setAliases} placeholder="Alias" addLabel="Add Alias" />
        </Field>
      </div>

      <Field label="URLs" fieldProvenance={performer.fieldProvenance} fieldKey="urls">
        <StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" />
      </Field>

      {/* Tags */}
      <Field label="Tags" fieldProvenance={performer.fieldProvenance} fieldKey="tags">
        <SelectedTagChips tags={selectedTags} onRemove={(tag) => setSelectedTagIds((current) => current.filter((id) => id !== tag.id))} className="mb-2 flex flex-wrap gap-1.5" provenanceById={tagProvenanceById} />
        <input
          ref={tagAutocomplete.inputRef}
          {...tagAutocomplete.inputProps}
          type="text"
          value={tagSearch}
          placeholder="Search tags..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {trimmedTagSearch && tagAutocomplete.isOpen && (
          <div
            ref={tagAutocomplete.listboxRef}
            {...tagAutocomplete.listboxProps}
            className="max-h-32 overflow-y-auto bg-card rounded border border-border"
          >
            {tagResultsLoading ? (
              <div className="px-3 py-1.5 text-sm text-secondary">Loading...</div>
            ) : filteredTags.length === 0 && !showTagCreateOption ? (
              <div className="px-3 py-1.5 text-sm text-secondary">No tags found</div>
            ) : null}
            {filteredTags.map((tag, index) => (
              <button
                key={tag.id}
                {...tagAutocomplete.getOptionProps<HTMLButtonElement>(tagAutocompleteItems[index])}
                type="button"
                className={`block w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-card ${tagAutocomplete.activeKey === tagAutocompleteItems[index].key ? "bg-card" : ""}`}
              >
                {tag.name}
              </button>
            ))}
            {showTagCreateOption ? (
              <button
                {...tagAutocomplete.getOptionProps<HTMLButtonElement>(tagAutocompleteItems[tagAutocompleteItems.length - 1])}
                type="button"
                disabled={tagCreateMutation.isPending}
                className={`flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-accent hover:bg-card disabled:opacity-50 ${tagAutocomplete.activeKey === tagAutocompleteItems[tagAutocompleteItems.length - 1].key ? "bg-card" : ""}`}
              >
                {tagCreateMutation.isPending ? (
                  <span className="text-secondary">Creating...</span>
                ) : (
                  <>
                    <Plus className="h-3 w-3" />
                    <span>Create &ldquo;{trimmedTagSearch}&rdquo;</span>
                  </>
                )}
              </button>
            ) : null}
          </div>
        )}
      </Field>

      <Field label="Remote IDs" fieldProvenance={performer.fieldProvenance} fieldKey="remoteIds">
        <RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} />
      </Field>
      <Field label="Custom Fields" fieldProvenance={performer.fieldProvenance} fieldKey="customFields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} entityType="performer" />
      </Field>
      </div>

      {mutation.error && (
        <div className="bg-red-900/50 border border-red-700 text-red-300 rounded p-2 mb-4 text-sm">
          {(mutation.error as Error).message}
        </div>
      )}

      <div className="flex justify-end gap-3">
        <button onClick={onClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
        <SaveButton loading={mutation.isPending} onClick={handleSave} />
      </div>
    </EditModal>
  );
}
