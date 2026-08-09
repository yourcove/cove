import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import {
  apiTokensApi,
  auditApi,
  contentRulesApi,
  rolesApi,
  shareLinksApi,
  usersApi,
  type ApiTokenIssuedRow,
  type ApiTokenRow,
  type ContentRuleRow,
  type EntityOverrideRow,
  type InviteTokenRow,
  type PermissionInfo,
  type RoleRow,
  type ShareLinkIssuedRow,
  type ShareLinkRow,
  type UserRow,
} from "../../api/client";
import { useAuth } from "../../auth/AuthContext";
import { SettingsButton as Btn, SettingsField as Field, SettingsSection as Section } from "../../components/SettingsPrimitives";
import { EditModal } from "../../components/EditModal";
import { buildRoutePath } from "../../router/location";
import { EntityReferenceSelector } from "../../components/EntityReferenceSelector";
import { formatDateTime } from "../../utils/dateFormat";

const ENTITY_KINDS = ["video", "performer", "tag", "studio", "gallery", "image", "group", "segment"] as const;
const SCOPE_KINDS = ["all", "tag", "studio", "attribute", "expression"] as const;
const APPLIES_TO = ["read", "write", "delete", "all"] as const;
const EFFECTS = ["deny", "allow"] as const;
const SIMPLE_SCOPE_KINDS = ["all", "tag", "studio", "attribute"] as const;
const ATTRIBUTE_OPERATORS = [
  { value: "exists", label: "Exists" },
  { value: "notExists", label: "Does not exist" },
  { value: "equals", label: "Equals" },
  { value: "notEquals", label: "Does not equal" },
  { value: "contains", label: "Contains" },
  { value: "startsWith", label: "Starts with" },
  { value: "endsWith", label: "Ends with" },
  { value: "regex", label: "Matches regex" },
  { value: "in", label: "Matches any of" },
  { value: "gt", label: "Greater than" },
  { value: "gte", label: "Greater than or equal" },
  { value: "lt", label: "Less than" },
  { value: "lte", label: "Less than or equal" },
] as const;
const ENTITY_LIST_ROUTES: Record<string, string> = {
  video: "videos",
  performer: "performers",
  tag: "tags",
  studio: "studios",
  gallery: "galleries",
  image: "images",
  group: "groups",
  segment: "segments",
};

const inputClassName = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none disabled:cursor-not-allowed disabled:opacity-60";
const checkboxClassName = "h-4 w-4 rounded border-border bg-card text-accent focus:ring-accent/40";
const checkboxCardClassName = "inline-flex items-center gap-2 rounded-lg border border-border bg-card px-2.5 py-1.5 text-sm text-foreground transition-colors hover:border-accent/50 hover:bg-card-hover";

function formatEntityKind(entityKind: string) {
  return entityKind;
}

type SimpleScopeKind = (typeof SIMPLE_SCOPE_KINDS)[number];
type AttributeOperator = (typeof ATTRIBUTE_OPERATORS)[number]["value"];
type ExpressionOperator = "and" | "or" | "not";

interface ContentRuleScopeDraft {
  tagId?: number;
  studioId?: number;
  attributePath: string;
  attributeOperator: AttributeOperator;
  attributeValue: string;
}

interface ContentRuleExpressionRuleDraft extends ContentRuleScopeDraft {
  id: string;
  scopeKind: SimpleScopeKind;
}

type ScopeBuildResult =
  | { ok: true; value: Record<string, unknown> }
  | { ok: false; error: string };

let contentRuleDraftId = 0;

function nextContentRuleDraftId() {
  contentRuleDraftId += 1;
  return `content-rule-${contentRuleDraftId}`;
}

function createEmptyScopeDraft(): ContentRuleScopeDraft {
  return {
    attributePath: "",
    attributeOperator: "equals",
    attributeValue: "",
  };
}

function createExpressionRuleDraft(scopeKind: SimpleScopeKind = "tag"): ContentRuleExpressionRuleDraft {
  return {
    id: nextContentRuleDraftId(),
    scopeKind,
    ...createEmptyScopeDraft(),
  };
}

function parseLooseScalar(value: string): string | number | boolean | null {
  const trimmed = value.trim();
  if (!trimmed.length) {
    return "";
  }

  if (/^-?\d+(\.\d+)?$/.test(trimmed)) {
    return Number(trimmed);
  }

  if (/^(true|false)$/i.test(trimmed)) {
    return trimmed.toLowerCase() === "true";
  }

  if (trimmed.toLowerCase() === "null") {
    return null;
  }

  return trimmed;
}

function buildSimpleScopeValue(scopeKind: SimpleScopeKind, draft: ContentRuleScopeDraft): ScopeBuildResult {
  switch (scopeKind) {
    case "all":
      return { ok: true, value: {} };
    case "tag":
      return typeof draft.tagId === "number"
        ? { ok: true, value: { tagId: draft.tagId } }
        : { ok: false, error: "Select a tag for this rule." };
    case "studio":
      return typeof draft.studioId === "number"
        ? { ok: true, value: { studioId: draft.studioId } }
        : { ok: false, error: "Select a studio for this rule." };
    case "attribute": {
      const path = draft.attributePath.trim();
      if (!path) {
        return { ok: false, error: "Enter an attribute path to evaluate." };
      }

      if (draft.attributeOperator === "exists") {
        return { ok: true, value: { path, exists: true } };
      }

      if (draft.attributeOperator === "notExists") {
        return { ok: true, value: { path, exists: false } };
      }

      const rawValue = draft.attributeValue.trim();
      if (!rawValue) {
        return { ok: false, error: "Enter a value for this attribute rule." };
      }

      if (draft.attributeOperator === "in") {
        const values = rawValue
          .split(/[\r\n,]+/)
          .map((item) => item.trim())
          .filter(Boolean)
          .map(parseLooseScalar);

        if (!values.length) {
          return { ok: false, error: "Enter at least one value for the list match." };
        }

        return { ok: true, value: { path, in: values } };
      }

      return {
        ok: true,
        value: {
          path,
          [draft.attributeOperator]: parseLooseScalar(rawValue),
        },
      };
    }
    default:
      return { ok: false, error: "Unsupported scope kind." };
  }
}

function buildScopeValue(scopeKind: (typeof SCOPE_KINDS)[number], draft: ContentRuleScopeDraft, expressionOperator: ExpressionOperator, expressionRules: ContentRuleExpressionRuleDraft[]): ScopeBuildResult {
  if (scopeKind !== "expression") {
    return buildSimpleScopeValue(scopeKind, draft);
  }

  if (!expressionRules.length) {
    return { ok: false, error: "Add at least one expression rule." };
  }

  const builtRules: Array<{ scopeKind: SimpleScopeKind; scopeValue: Record<string, unknown> }> = [];
  for (const rule of expressionRules) {
    const builtRule = buildSimpleScopeValue(rule.scopeKind, rule);
    if (!builtRule.ok) {
      return { ok: false, error: builtRule.error };
    }

    builtRules.push({ scopeKind: rule.scopeKind, scopeValue: builtRule.value });
  }

  if (expressionOperator === "not") {
    return {
      ok: true,
      value: builtRules.length === 1
        ? { op: "not", rule: builtRules[0] }
        : { op: "not", rule: { scopeKind: "expression", scopeValue: { op: "or", rules: builtRules } } },
    };
  }

  return { ok: true, value: { op: expressionOperator, rules: builtRules } };
}

function parseScopeValue(scopeValue: string): Record<string, unknown> | null {
  if (!scopeValue.trim()) {
    return {};
  }

  try {
    const parsed = JSON.parse(scopeValue) as unknown;
    return parsed && typeof parsed === "object" ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

function formatScopeScalar(value: unknown): string {
  if (Array.isArray(value)) {
    return value.map((item) => formatScopeScalar(item)).join(", ");
  }

  if (typeof value === "string") {
    return value;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  if (value === null) {
    return "null";
  }

  return JSON.stringify(value);
}

function formatParsedScopeSummary(scopeKind: string, scopeValue: Record<string, unknown>): string {
  switch (scopeKind) {
    case "all":
      return "all content";
    case "tag":
      return `tag #${scopeValue.tagId ?? "?"}`;
    case "studio":
      return `studio #${scopeValue.studioId ?? "?"}`;
    case "attribute": {
      const path = String(scopeValue.path ?? scopeValue.field ?? "attribute");
      if (Object.prototype.hasOwnProperty.call(scopeValue, "exists")) {
        return `${path} ${scopeValue.exists ? "exists" : "does not exist"}`;
      }

      const operator = ["equals", "notEquals", "contains", "startsWith", "endsWith", "regex", "in", "gt", "gte", "lt", "lte"]
        .find((key) => Object.prototype.hasOwnProperty.call(scopeValue, key));
      return operator ? `${path} ${operator} ${formatScopeScalar(scopeValue[operator])}` : `${path} attribute rule`;
    }
    case "expression": {
      const op = String(scopeValue.op ?? "").toLowerCase();
      if (op === "not") {
        const child = scopeValue.rule as Record<string, unknown> | undefined;
        if (!child) return "not rule";
        return `not (${formatParsedScopeSummary(String(child.scopeKind ?? "all"), (child.scopeValue as Record<string, unknown>) ?? {})})`;
      }

      const rules = Array.isArray(scopeValue.rules) ? scopeValue.rules as Array<Record<string, unknown>> : [];
      const summaries = rules.map((rule) => formatParsedScopeSummary(String(rule.scopeKind ?? "all"), (rule.scopeValue as Record<string, unknown>) ?? {}));
      if (!summaries.length) {
        return "expression rule";
      }

      const separator = op === "or" ? " OR " : " AND ";
      return summaries.join(separator);
    }
    default:
      return `${scopeKind} rule`;
  }
}

function formatContentRuleScope(rule: Pick<ContentRuleRow, "scopeKind" | "scopeValue">): string {
  const parsed = parseScopeValue(rule.scopeValue);
  if (!parsed) {
    return rule.scopeKind === "all" ? "all content" : `${rule.scopeKind} rule`;
  }

  return formatParsedScopeSummary(rule.scopeKind, parsed);
}

function SingleEntitySelector({ entityType, value, onChange, placeholder }: { entityType: "tags" | "studios"; value?: number; onChange: (value: number | undefined) => void; placeholder: string }) {
  return <EntityReferenceSelector entityType={entityType === "tags" ? "tag" : "studio"} value={value} onChange={onChange} placeholder={placeholder} inputClassName="input" />;
}

function ContentRuleScopeFields({ scopeKind, draft, onChange }: { scopeKind: SimpleScopeKind; draft: ContentRuleScopeDraft; onChange: (update: Partial<ContentRuleScopeDraft>) => void }) {
  if (scopeKind === "all") {
    return <p className="text-sm text-secondary">This rule applies to all entities of the selected type.</p>;
  }

  if (scopeKind === "tag") {
    return <SingleEntitySelector entityType="tags" value={draft.tagId} onChange={(value) => onChange({ tagId: value })} placeholder="Search tags..." />;
  }

  if (scopeKind === "studio") {
    return <SingleEntitySelector entityType="studios" value={draft.studioId} onChange={(value) => onChange({ studioId: value })} placeholder="Search studios..." />;
  }

  return (
    <div className="space-y-3">
      <Field label="Attribute path">
        <input className={inputClassName} value={draft.attributePath} onChange={(event) => onChange({ attributePath: event.target.value })} placeholder="details or rating" />
      </Field>
      <Field label="Operator">
        <select className={inputClassName} value={draft.attributeOperator} onChange={(event) => onChange({ attributeOperator: event.target.value as AttributeOperator })}>
          {ATTRIBUTE_OPERATORS.map((operator) => <option key={operator.value} value={operator.value}>{operator.label}</option>)}
        </select>
      </Field>
      {draft.attributeOperator !== "exists" && draft.attributeOperator !== "notExists" ? (
        <Field label={draft.attributeOperator === "in" ? "Values" : "Value"}>
          {draft.attributeOperator === "in" ? (
            <textarea className={`${inputClassName} min-h-24`} value={draft.attributeValue} onChange={(event) => onChange({ attributeValue: event.target.value })} placeholder="one value per line or comma-separated" />
          ) : (
            <input className={inputClassName} value={draft.attributeValue} onChange={(event) => onChange({ attributeValue: event.target.value })} placeholder="Value to compare" />
          )}
        </Field>
      ) : null}
    </div>
  );
}

function UserStatus({ user }: { user: UserRow }) {
  if (user.isLocked) {
    return <span className="text-amber-400">locked</span>;
  }

  if (!user.isActive) {
    return <span className="text-secondary">disabled</span>;
  }

  if (!user.hasPassword) {
    return <span className="text-amber-400">password required</span>;
  }

  return <span className="text-emerald-400">active</span>;
}

// =========================================================================
// USERS
// =========================================================================
export function UsersTab() {
  const auth = useAuth();
  const qc = useQueryClient();
  const usersQ = useQuery({ queryKey: ["admin", "users"], queryFn: usersApi.list });
  const rolesQ = useQuery({ queryKey: ["admin", "roles"], queryFn: rolesApi.list });
  const canWriteUsers = auth.hasPermission("users.write");
  const canInviteUsers = auth.hasPermission("users.invite");
  const canDeleteUsers = auth.hasPermission("users.delete");

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<UserRow | null>(null);
  const [inviteUser, setInviteUser] = useState<UserRow | null>(null);

  const removeM = useMutation({
    mutationFn: (id: number) => usersApi.remove(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "users"] }),
  });
  const unlockM = useMutation({
    mutationFn: (id: number) => usersApi.unlock(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "users"] }),
  });
  const activeM = useMutation({
    mutationFn: ({ id, isActive }: { id: number; isActive: boolean }) => usersApi.update(id, { isActive }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "users"] }),
  });

  return (
    <div className="space-y-6">
      <Section
        title="Users"
        description="Local user accounts and their role assignments."
        actions={canWriteUsers ? <Btn variant="primary" onClick={() => setCreating(true)}>+ New user</Btn> : null}
      >
        {usersQ.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
        {usersQ.error ? <p className="text-sm text-red-400">Failed to load users.</p> : null}
        {usersQ.data ? (
          <>
          <div className="space-y-3 md:hidden">
            {usersQ.data.map((userRow) => (
              <article key={userRow.id} className="rounded-xl border border-border bg-card p-3 text-sm">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="break-words font-medium">{userRow.username}{userRow.isSystem ? <span className="ml-1 text-xs text-secondary">(system)</span> : null}</div>
                    <div className="mt-0.5 break-words text-xs text-secondary">{userRow.displayName ?? "No display name"}</div>
                  </div>
                  <UserStatus user={userRow} />
                </div>
                <div className="mt-3 flex flex-wrap gap-1">
                  {userRow.roles.length ? userRow.roles.map((role) => <span key={role} className="rounded border border-border bg-surface px-2 py-0.5 text-xs">{role}</span>) : <span className="text-xs text-secondary">No roles</span>}
                </div>
                <div className="mt-3 text-xs text-secondary">Last login: {userRow.lastLoginAt ? formatDateTime(userRow.lastLoginAt) : "never"}</div>
                <div className="mt-3 flex flex-wrap gap-2">
                  {canWriteUsers ? <Btn onClick={() => setEditing(userRow)}>Edit</Btn> : null}
                  {canInviteUsers ? <Btn onClick={() => setInviteUser(userRow)}>{userRow.hasPassword ? "Reset password" : "Create password invite"}</Btn> : null}
                  {userRow.isLocked && canWriteUsers ? <Btn onClick={() => unlockM.mutate(userRow.id)}>Unlock</Btn> : null}
                  {canWriteUsers && !userRow.isSystem ? (
                    <Btn onClick={() => activeM.mutate({ id: userRow.id, isActive: !userRow.isActive })}>{userRow.isActive ? "Disable" : "Enable"}</Btn>
                  ) : null}
                  {canDeleteUsers && !userRow.isSystem ? (
                    <Btn variant="danger" onClick={() => { if (confirm(`Delete user "${userRow.username}"?`)) removeM.mutate(userRow.id); }}>Delete</Btn>
                  ) : null}
                </div>
              </article>
            ))}
          </div>
          <div className="hidden overflow-x-auto md:block">
            <table className="min-w-full text-sm">
              <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
                <tr>
                  <th className="px-2 py-2">Username</th>
                  <th className="px-2 py-2">Display name</th>
                  <th className="px-2 py-2">Roles</th>
                  <th className="px-2 py-2">Status</th>
                  <th className="px-2 py-2">Last login</th>
                  <th className="px-2 py-2 text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {usersQ.data.map((u) => (
                  <tr key={u.id} className="border-b border-border/40">
                    <td className="px-2 py-2 font-medium">{u.username}{u.isSystem ? <span className="ml-1 text-xs text-secondary">(system)</span> : null}</td>
                    <td className="px-2 py-2">{u.displayName ?? <span className="text-secondary">—</span>}</td>
                    <td className="px-2 py-2">
                      {u.roles.length ? (
                        <div className="flex flex-wrap gap-1">
                          {u.roles.map((role) => <span key={role} className="rounded border border-border bg-card px-2 py-0.5 text-xs">{role}</span>)}
                        </div>
                      ) : <span className="text-secondary">—</span>}
                    </td>
                    <td className="px-2 py-2">
                      <UserStatus user={u} />
                    </td>
                    <td className="px-2 py-2 text-secondary">{u.lastLoginAt ? formatDateTime(u.lastLoginAt) : "—"}</td>
                    <td className="px-2 py-2">
                      <div className="flex flex-wrap justify-end gap-1">
                      {canWriteUsers ? <Btn onClick={() => setEditing(u)}>Edit</Btn> : null}
                      {canInviteUsers ? <Btn onClick={() => setInviteUser(u)}>{u.hasPassword ? "Reset password" : "Create password invite"}</Btn> : null}
                      {u.isLocked && canWriteUsers ? <Btn onClick={() => unlockM.mutate(u.id)}>Unlock</Btn> : null}
                      {canWriteUsers && !u.isSystem ? (
                        <Btn onClick={() => activeM.mutate({ id: u.id, isActive: !u.isActive })}>{u.isActive ? "Disable" : "Enable"}</Btn>
                      ) : null}
                      {canDeleteUsers && !u.isSystem ? (
                        <Btn variant="danger" onClick={() => { if (confirm(`Delete user "${u.username}"?`)) removeM.mutate(u.id); }}>Delete</Btn>
                      ) : null}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          </>
        ) : null}
      </Section>

      {creating ? (
        <CreateUserDialog roles={rolesQ.data ?? []} canInvite={canInviteUsers} onClose={() => setCreating(false)} />
      ) : null}
      {editing ? (
        <EditUserDialog user={editing} roles={rolesQ.data ?? []} onClose={() => setEditing(null)} />
      ) : null}
      {inviteUser ? (
        <InviteDialog user={inviteUser} onClose={() => setInviteUser(null)} />
      ) : null}
    </div>
  );
}

function CreateUserDialog({ roles, canInvite, onClose }: { roles: RoleRow[]; canInvite: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [selectedRoles, setSelectedRoles] = useState<string[]>([]);
  const [credentialMode, setCredentialMode] = useState<"invite" | "password">(canInvite ? "invite" : "password");
  const [mustChange, setMustChange] = useState(true);
  const [inviteToken, setInviteToken] = useState<InviteTokenRow | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const m = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      if (credentialMode === "invite" && canInvite) {
        return await usersApi.createInvite({
          username: username.trim() || undefined,
          displayName: displayName || undefined,
          email: email || undefined,
          roles: selectedRoles,
        });
      }

      const created = await usersApi.create({
        username,
        password,
        displayName: displayName || undefined,
        email: email || undefined,
        roles: selectedRoles,
        mustChangePassword: mustChange,
      });
      return null;
    },
    onSuccess: (invite) => {
      qc.invalidateQueries({ queryKey: ["admin", "users"] });
      if (invite) {
        setInviteToken(invite);
      } else {
        onClose();
      }
    },
    onError: (e: any) => setErr(e?.message ?? "Failed"),
  });

  if (inviteToken) {
    return (
      <Modal title="Invite link" onClose={onClose}>
        <InviteLinkPanel invite={inviteToken} onClose={onClose} />
      </Modal>
    );
  }

  return (
    <Modal title="Create user" onClose={onClose}>
      <div className="space-y-3">
        <Field label={credentialMode === "invite" ? "Username (optional)" : "Username"}><input className={inputClassName} value={username} onChange={(e) => setUsername(e.target.value)} /></Field>
        <Field label="Display name"><input className={inputClassName} value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></Field>
        <Field label="Email"><input className={inputClassName} value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
        <Field label="Roles">
          <div className="flex flex-wrap gap-2">
            {roles.map(r => (
              <label key={r.name} className={checkboxCardClassName}>
                <input className={checkboxClassName} type="checkbox" checked={selectedRoles.includes(r.name)} onChange={(e) => setSelectedRoles(s => e.target.checked ? [...s, r.name] : s.filter(x => x !== r.name))} />
                {r.name}
              </label>
            ))}
          </div>
        </Field>
        <Field label="Password setup">
          <div className="grid grid-cols-2 gap-2 rounded-xl border border-border bg-card p-1">
            <button
              type="button"
              onClick={() => setCredentialMode("password")}
              className={`rounded-lg px-3 py-2 text-sm font-medium transition-colors ${credentialMode === "password" ? "bg-accent text-white" : "text-secondary hover:bg-card-hover hover:text-foreground"}`}
            >
              Set now
            </button>
            <button
              type="button"
              disabled={!canInvite}
              onClick={() => setCredentialMode("invite")}
              className={`rounded-lg px-3 py-2 text-sm font-medium transition-colors ${credentialMode === "invite" ? "bg-accent text-white" : "text-secondary hover:bg-card-hover hover:text-foreground"} disabled:opacity-50`}
            >
              Invite link
            </button>
          </div>
        </Field>
        {credentialMode === "password" ? (
          <>
            <Field label="Password"><input className={inputClassName} type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
            <label className="inline-flex items-center gap-2 text-sm">
              <input className={checkboxClassName} type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
              Force password change at next login
            </label>
          </>
        ) : null}
        {err ? <p className="text-sm text-red-400">{err}</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={onClose}>Cancel</Btn>
          <Btn variant="primary" onClick={() => m.mutate()} disabled={(credentialMode === "password" && (!username || !password)) || m.isPending}>{credentialMode === "invite" ? "Create invite" : "Create"}</Btn>
        </div>
      </div>
    </Modal>
  );
}

function EditUserDialog({ user, roles, onClose }: { user: UserRow; roles: RoleRow[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [displayName, setDisplayName] = useState(user.displayName ?? "");
  const [email, setEmail] = useState(user.email ?? "");
  const [isActive, setIsActive] = useState(user.isActive);
  const [selectedRoles, setSelectedRoles] = useState<string[]>(user.roles);
  const [err, setErr] = useState<string | null>(null);

  const updateM = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      await usersApi.update(user.id, { displayName: displayName || undefined, email: email || undefined, isActive });
      await usersApi.setRoles(user.id, selectedRoles);
    },
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["admin", "users"] }); onClose(); },
    onError: (e: any) => setErr(e?.message ?? "Failed"),
  });

  return (
    <Modal title={`Edit ${user.username}`} onClose={onClose}>
      <div className="space-y-3">
        <Field label="Display name"><input className={inputClassName} value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></Field>
        <Field label="Email"><input className={inputClassName} value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
        <label className="inline-flex items-center gap-2 text-sm">
          <input className={checkboxClassName} type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
          Active
        </label>
        <Field label="Roles">
          <div className="flex flex-wrap gap-2">
            {roles.map(r => (
              <label key={r.name} className={checkboxCardClassName}>
                <input className={checkboxClassName} type="checkbox" checked={selectedRoles.includes(r.name)} disabled={user.isSystem && r.name === "Owner"} onChange={(e) => setSelectedRoles(s => e.target.checked ? [...s, r.name] : s.filter(x => x !== r.name))} />
                {r.name}
              </label>
            ))}
          </div>
        </Field>
        {err ? <p className="text-sm text-red-400">{err}</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={onClose}>Cancel</Btn>
          <Btn variant="primary" onClick={() => updateM.mutate()} disabled={updateM.isPending}>Save</Btn>
        </div>
      </div>
    </Modal>
  );
}

function InviteDialog({ user, onClose }: { user: UserRow; onClose: () => void }) {
  const qc = useQueryClient();
  const [invite, setInvite] = useState<InviteTokenRow | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const m = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => usersApi.invite(user.id),
    onSuccess: (nextInvite) => {
      setInvite(nextInvite);
      qc.invalidateQueries({ queryKey: ["admin", "users"] });
    },
    onError: (e: any) => setErr(e?.message ?? "Failed"),
  });

  useEffect(() => {
    m.mutate();
    // Run once per opened dialog.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Modal title={`Invite ${user.username}`} onClose={onClose}>
      <div className="space-y-3">
        {m.isPending ? <p className="text-sm text-secondary">Generating invite link...</p> : null}
        {invite ? <InviteLinkPanel invite={invite} onClose={onClose} /> : null}
        {err ? <p className="text-sm text-red-400">{err}</p> : null}
        {!invite ? <div className="flex justify-end gap-2 pt-2"><Btn onClick={onClose}>Close</Btn></div> : null}
      </div>
    </Modal>
  );
}

function InviteLinkPanel({ invite, onClose }: { invite: InviteTokenRow; onClose: () => void }) {
  return (
    <div className="space-y-3">
      <Field label="Invite URL"><input className={`${inputClassName} font-mono text-xs`} value={invite.url} readOnly /></Field>
      <p className="text-sm text-amber-400">Copy this link now. It will not be shown again.</p>
      <div className="flex justify-end gap-2 pt-2">
        <Btn onClick={() => navigator.clipboard.writeText(invite.token)}>Copy raw token</Btn>
        <Btn variant="primary" onClick={() => { navigator.clipboard.writeText(invite.url); onClose(); }}>Copy link & close</Btn>
      </div>
    </div>
  );
}

// =========================================================================
// ROLES
// =========================================================================
export function RolesTab() {
  const auth = useAuth();
  const qc = useQueryClient();
  const rolesQ = useQuery({ queryKey: ["admin", "roles"], queryFn: rolesApi.list });
  const permsQ = useQuery({ queryKey: ["admin", "permissions"], queryFn: rolesApi.permissions });

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<RoleRow | null>(null);

  const removeM = useMutation({
    mutationFn: (id: number) => rolesApi.remove(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "roles"] }),
  });

  return (
    <div className="space-y-6">
      <Section
        title="Roles"
        description="Roles bundle permissions and are assigned to users."
        actions={auth.hasPermission("roles.write") ? <Btn variant="primary" onClick={() => setCreating(true)}>+ New role</Btn> : null}
      >
        {rolesQ.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
        {rolesQ.data ? (
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
                <tr>
                  <th className="px-2 py-2">Name</th>
                  <th className="px-2 py-2">Description</th>
                  <th className="px-2 py-2">Source</th>
                  <th className="px-2 py-2">Permissions</th>
                  <th className="px-2 py-2 text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {rolesQ.data.map((r) => (
                  <tr key={r.id} className="border-b border-border/40">
                    <td className="px-2 py-2 font-medium">{r.name}{r.isBuiltin ? <span className="ml-1 text-xs text-secondary">(builtin)</span> : null}</td>
                    <td className="px-2 py-2 text-secondary">{r.description ?? "—"}</td>
                    <td className="px-2 py-2 text-secondary">{r.source}</td>
                    <td className="px-2 py-2 text-secondary">{r.permissions.length}</td>
                    <td className="px-2 py-2 text-right space-x-1">
                      {auth.hasPermission("roles.write") ? <Btn onClick={() => setEditing(r)}>{r.isBuiltin ? "View" : "Edit"}</Btn> : null}
                      {auth.hasPermission("roles.delete") && !r.isBuiltin ? (
                        <Btn variant="danger" onClick={() => { if (confirm(`Delete role "${r.name}"?`)) removeM.mutate(r.id); }}>Delete</Btn>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </Section>

      {creating ? <RoleEditor permissions={permsQ.data ?? []} onClose={() => setCreating(false)} /> : null}
      {editing ? <RoleEditor role={editing} permissions={permsQ.data ?? []} onClose={() => setEditing(null)} /> : null}
    </div>
  );
}

function RoleEditor({ role, permissions, onClose }: { role?: RoleRow; permissions: PermissionInfo[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [name, setName] = useState(role?.name ?? "");
  const [description, setDescription] = useState(role?.description ?? "");
  const [perms, setPerms] = useState<string[]>(role?.permissions ?? []);
  const [err, setErr] = useState<string | null>(null);
  const isReadOnly = !!role?.isBuiltin;

  const grouped = useMemo(() => {
    const m = new Map<string, PermissionInfo[]>();
    for (const p of permissions) {
      const arr = m.get(p.category) ?? [];
      arr.push(p);
      m.set(p.category, arr);
    }
    return Array.from(m.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [permissions]);

  const m = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => role
      ? rolesApi.update(role.id, { description: description || undefined, permissions: perms })
      : rolesApi.create({ name, description: description || undefined, permissions: perms }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["admin", "roles"] }); onClose(); },
    onError: (e: any) => setErr(e?.message ?? "Failed"),
  });

  return (
    <Modal title={role ? `${isReadOnly ? "View" : "Edit"} role: ${role.name}` : "New role"} onClose={onClose} wide>
      <div className="space-y-3">
        {!role ? <Field label="Name"><input className={inputClassName} value={name} onChange={(e) => setName(e.target.value)} /></Field> : null}
        <Field label="Description"><input className={inputClassName} value={description} onChange={(e) => setDescription(e.target.value)} disabled={isReadOnly} /></Field>
        <Field label={`Permissions (${perms.length} selected)`}>
          <div className="max-h-96 overflow-auto rounded-xl border border-border bg-card/70 p-3 space-y-3">
            {grouped.map(([cat, list]) => (
              <div key={cat}>
                <h4 className="mb-1 text-xs font-semibold uppercase tracking-wide text-secondary">{cat}</h4>
                <div className="grid grid-cols-1 gap-1 md:grid-cols-2">
                  {list.map(p => (
                    <label key={p.key} className="inline-flex items-start gap-1.5 text-sm">
                      <input className={checkboxClassName} type="checkbox" disabled={isReadOnly} checked={perms.includes(p.key)} onChange={(e) => setPerms(s => e.target.checked ? [...s, p.key] : s.filter(x => x !== p.key))} />
                      <span>
                        <code className="text-xs">{p.key}</code>
                        {p.dangerous ? <span className="ml-1 text-red-400 text-xs">(dangerous)</span> : null}
                        <div className="text-xs text-secondary">{p.description}</div>
                      </span>
                    </label>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </Field>
        {err ? <p className="text-sm text-red-400">{err}</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={onClose}>{isReadOnly ? "Close" : "Cancel"}</Btn>
          {!isReadOnly ? <Btn variant="primary" onClick={() => m.mutate()} disabled={(!role && !name) || m.isPending}>{role ? "Save" : "Create"}</Btn> : null}
        </div>
      </div>
    </Modal>
  );
}

// =========================================================================
// AUDIT
// =========================================================================
export function AuditTab() {
  const [page, setPage] = useState(1);
  const [perPage] = useState(50);
  const [action, setAction] = useState("");
  const [actor, setActor] = useState("");
  const [outcome, setOutcome] = useState("");

  const q = useQuery({
    queryKey: ["admin", "audit", { page, perPage, action, actor, outcome }],
    queryFn: () => auditApi.list({ page, perPage, action: action || undefined, actor: actor || undefined, outcome: outcome || undefined }),
  });

  const totalPages = q.data ? Math.max(1, Math.ceil(q.data.totalCount / perPage)) : 1;

  return (
    <Section title="Audit log" description="Records of authentication, authorization, and administrative actions.">
      <div className="mb-3 flex flex-wrap items-end gap-2">
        <Field label="Action"><input className={inputClassName} value={action} onChange={(e) => { setAction(e.target.value); setPage(1); }} placeholder="e.g. user.create" /></Field>
        <Field label="Actor"><input className={inputClassName} value={actor} onChange={(e) => { setActor(e.target.value); setPage(1); }} placeholder="username" /></Field>
        <Field label="Outcome">
          <select className={inputClassName} value={outcome} onChange={(e) => { setOutcome(e.target.value); setPage(1); }}>
            <option value="">Any</option>
            <option value="success">success</option>
            <option value="failure">failure</option>
            <option value="denied">denied</option>
          </select>
        </Field>
        <Btn onClick={() => q.refetch()}>Refresh</Btn>
      </div>
      {q.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
      {q.data ? (
        <>
          <div className="overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
                <tr>
                  <th className="px-2 py-2">When</th>
                  <th className="px-2 py-2">Actor</th>
                  <th className="px-2 py-2">Action</th>
                  <th className="px-2 py-2">Target</th>
                  <th className="px-2 py-2">Outcome</th>
                  <th className="px-2 py-2">IP</th>
                  <th className="px-2 py-2">Detail</th>
                </tr>
              </thead>
              <tbody>
                {q.data.items.map((e) => (
                  <tr key={e.id} className="border-b border-border/40">
                    <td className="px-2 py-2 whitespace-nowrap text-secondary">{formatDateTime(e.occurredAt)}</td>
                    <td className="px-2 py-2">{e.actorUsername ?? <span className="text-secondary">{e.actorKind}</span>}</td>
                    <td className="px-2 py-2"><code className="text-xs">{e.action}</code></td>
                    <td className="px-2 py-2 text-secondary">{e.targetKind ? `${e.targetKind}:${e.targetId ?? ""}` : "—"}</td>
                    <td className="px-2 py-2">
                      <span className={
                        e.outcome === "success" ? "text-emerald-400" :
                        e.outcome === "denied" ? "text-amber-400" :
                        e.outcome === "failure" ? "text-red-400" : ""}>
                        {e.outcome}
                      </span>
                    </td>
                    <td className="px-2 py-2 text-secondary">{e.ip ?? "—"}</td>
                    <td className="px-2 py-2 text-secondary text-xs max-w-md truncate" title={e.detail ?? undefined}>{e.detail ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="mt-3 flex items-center justify-between text-sm">
            <span className="text-secondary">{q.data.totalCount} total · page {page} of {totalPages}</span>
            <div className="space-x-1">
              <Btn onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page <= 1}>Prev</Btn>
              <Btn onClick={() => setPage(p => Math.min(totalPages, p + 1))} disabled={page >= totalPages}>Next</Btn>
            </div>
          </div>
        </>
      ) : null}
    </Section>
  );
}

export function ContentRulesTab() {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canWrite = hasPermission("roles.write");
  const [filterRoleId, setFilterRoleId] = useState<number | "">("");
  const [showCreate, setShowCreate] = useState(false);

  const rolesQ = useQuery({ queryKey: ["admin", "roles"], queryFn: rolesApi.list });
  const rulesQ = useQuery({
    queryKey: ["admin", "content-rules", filterRoleId || null],
    queryFn: () => contentRulesApi.list(typeof filterRoleId === "number" ? filterRoleId : undefined),
  });
  const overridesQ = useQuery({
    queryKey: ["admin", "entity-overrides", filterRoleId || null],
    queryFn: () => contentRulesApi.listOverrides(typeof filterRoleId === "number" ? filterRoleId : undefined),
  });

  const removeRule = useMutation({
    mutationFn: (id: number) => contentRulesApi.remove(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["admin", "content-rules"] }),
  });
  const removeOverride = useMutation({
    mutationFn: (id: number) => contentRulesApi.removeOverride(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["admin", "entity-overrides"] }),
  });

  return (
    <div className="space-y-6">
      <Section
        title="Content rules"
        description="Role-scoped visibility and write/delete rules. Matching deny rules win unless there is an explicit entity allow override."
        actions={canWrite ? <Btn variant="primary" onClick={() => setShowCreate(true)}>+ New rule</Btn> : null}
      >
        <div className="mb-4 flex flex-wrap items-end gap-3">
          <Field label="Role filter">
            <select
              className={inputClassName}
              value={filterRoleId}
              onChange={(event) => setFilterRoleId(event.target.value === "" ? "" : Number(event.target.value))}
            >
              <option value="">All roles</option>
              {rolesQ.data?.map((role) => (
                <option key={role.id} value={role.id}>{role.name}</option>
              ))}
            </select>
          </Field>
        </div>

        {rulesQ.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
        {rulesQ.data ? <ContentRuleTable rules={rulesQ.data} canWrite={canWrite} onDelete={(id) => removeRule.mutate(id)} /> : null}
      </Section>

      <Section title="Entity overrides" description="One-off allow or deny rules for specific entity ids.">
        {overridesQ.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
        {overridesQ.data ? <EntityOverrideTable overrides={overridesQ.data} canWrite={canWrite} onDelete={(id) => removeOverride.mutate(id)} /> : null}
      </Section>

      {showCreate && rolesQ.data ? <CreateContentRuleDialog roles={rolesQ.data} onClose={() => setShowCreate(false)} /> : null}
    </div>
  );
}

export function ApiTokensTab() {
  const queryClient = useQueryClient();
  const tokensQ = useQuery({ queryKey: ["admin", "api-tokens"], queryFn: () => apiTokensApi.list() });
  const [showCreate, setShowCreate] = useState(false);
  const [issued, setIssued] = useState<ApiTokenIssuedRow | null>(null);

  const revoke = useMutation({
    mutationFn: (id: string) => apiTokensApi.revoke(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["admin", "api-tokens"] }),
  });

  return (
    <Section
      title="API tokens"
      description="Long-lived personal access tokens. Scope is intersected with your own permissions."
      actions={<Btn variant="primary" onClick={() => setShowCreate(true)}>+ New token</Btn>}
    >
      {tokensQ.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
      {tokensQ.data ? <ApiTokensTable tokens={tokensQ.data} onRevoke={(id) => revoke.mutate(id)} /> : null}
      {showCreate ? <CreateApiTokenDialog onClose={() => setShowCreate(false)} onIssued={(token) => { setIssued(token); setShowCreate(false); }} /> : null}
      {issued ? <IssuedTokenDialog token={issued} onClose={() => setIssued(null)} /> : null}
    </Section>
  );
}

export function ShareLinksTab() {
  const queryClient = useQueryClient();
  const linksQ = useQuery({ queryKey: ["admin", "share-links"], queryFn: () => shareLinksApi.list() });
  const [showCreate, setShowCreate] = useState(false);
  const [issued, setIssued] = useState<ShareLinkIssuedRow | null>(null);

  const revoke = useMutation({
    mutationFn: (id: string) => shareLinksApi.revoke(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["admin", "share-links"] }),
  });

  return (
    <Section
      title="Share links"
      description="Anonymous, time-limited, optionally password-gated read-only links."
      actions={<Btn variant="primary" onClick={() => setShowCreate(true)}>+ New share link</Btn>}
    >
      {linksQ.isLoading ? <p className="text-sm text-secondary">Loading…</p> : null}
      {linksQ.data ? <ShareLinksTable links={linksQ.data} onRevoke={(id) => revoke.mutate(id)} /> : null}
      {showCreate ? <CreateShareLinkDialog onClose={() => setShowCreate(false)} onIssued={(link) => { setIssued(link); setShowCreate(false); }} /> : null}
      {issued ? <IssuedShareLinkDialog link={issued} onClose={() => setIssued(null)} /> : null}
    </Section>
  );
}

function ContentRuleTable({ rules, canWrite, onDelete }: { rules: ContentRuleRow[]; canWrite: boolean; onDelete: (id: number) => void }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
          <tr>
            <th className="px-2 py-2">Role</th>
            <th className="px-2 py-2">Entity</th>
            <th className="px-2 py-2">Effect</th>
            <th className="px-2 py-2">Scope</th>
            <th className="px-2 py-2">Applies to</th>
            <th className="px-2 py-2">Updated</th>
            <th className="px-2 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {rules.map((rule) => (
            <tr key={rule.id} className="border-b border-border/40">
              <td className="px-2 py-2 font-medium">{rule.roleName}</td>
              <td className="px-2 py-2">{formatEntityKind(rule.entityKind)}</td>
              <td className="px-2 py-2">
                <span className={rule.effect === "deny" ? "text-amber-400" : "text-emerald-400"}>{rule.effect}</span>
              </td>
              <td className="px-2 py-2 text-xs text-secondary">{formatContentRuleScope(rule)}</td>
              <td className="px-2 py-2">{rule.appliesTo}</td>
              <td className="px-2 py-2 text-secondary">{formatDateTime(rule.updatedAt)}</td>
              <td className="px-2 py-2 text-right">
                {canWrite ? <Btn variant="danger" onClick={() => { if (confirm("Delete this content rule?")) onDelete(rule.id); }}>Delete</Btn> : null}
              </td>
            </tr>
          ))}
          {rules.length === 0 ? (
            <tr>
              <td colSpan={7} className="px-2 py-4 text-center text-secondary">No content rules.</td>
            </tr>
          ) : null}
        </tbody>
      </table>
    </div>
  );
}

function EntityOverrideTable({ overrides, canWrite, onDelete }: { overrides: EntityOverrideRow[]; canWrite: boolean; onDelete: (id: number) => void }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
          <tr>
            <th className="px-2 py-2">Role</th>
            <th className="px-2 py-2">Entity</th>
            <th className="px-2 py-2">Id</th>
            <th className="px-2 py-2">Effect</th>
            <th className="px-2 py-2">Applies to</th>
            <th className="px-2 py-2">Created</th>
            <th className="px-2 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {overrides.map((overrideItem) => (
            <tr key={overrideItem.id} className="border-b border-border/40">
              <td className="px-2 py-2 font-medium">{overrideItem.roleName}</td>
              <td className="px-2 py-2">{formatEntityKind(overrideItem.entityKind)}</td>
              <td className="px-2 py-2 text-secondary">{overrideItem.entityId}</td>
              <td className="px-2 py-2">
                <span className={overrideItem.effect === "deny" ? "text-amber-400" : "text-emerald-400"}>{overrideItem.effect}</span>
              </td>
              <td className="px-2 py-2">{overrideItem.appliesTo}</td>
              <td className="px-2 py-2 text-secondary">{formatDateTime(overrideItem.createdAt)}</td>
              <td className="px-2 py-2 text-right">
                {canWrite ? <Btn variant="danger" onClick={() => { if (confirm("Delete this entity override?")) onDelete(overrideItem.id); }}>Delete</Btn> : null}
              </td>
            </tr>
          ))}
          {overrides.length === 0 ? (
            <tr>
              <td colSpan={7} className="px-2 py-4 text-center text-secondary">No entity overrides.</td>
            </tr>
          ) : null}
        </tbody>
      </table>
    </div>
  );
}

function ExpressionRuleBuilder({
  operator,
  rules,
  onOperatorChange,
  onRulesChange,
}: {
  operator: ExpressionOperator;
  rules: ContentRuleExpressionRuleDraft[];
  onOperatorChange: (operator: ExpressionOperator) => void;
  onRulesChange: (rules: ContentRuleExpressionRuleDraft[]) => void;
}) {
  const updateRule = (id: string, update: Partial<ContentRuleExpressionRuleDraft>) => {
    onRulesChange(rules.map((rule) => (rule.id === id ? { ...rule, ...update } : rule)));
  };

  const removeRule = (id: string) => {
    onRulesChange(rules.filter((rule) => rule.id !== id));
  };

  return (
    <div className="space-y-3 rounded-xl border border-border bg-card/70 p-3">
      <Field label="Expression mode">
        <select className={inputClassName} value={operator} onChange={(event) => onOperatorChange(event.target.value as ExpressionOperator)}>
          <option value="and">All of these rules must match</option>
          <option value="or">Any of these rules may match</option>
          <option value="not">None of these rules may match</option>
        </select>
      </Field>

      <div className="space-y-3">
        {rules.map((rule, index) => (
          <div key={rule.id} className="rounded-xl border border-border bg-surface p-3">
            <div className="mb-3 flex items-center justify-between gap-3">
              <span className="text-sm font-medium">Condition {index + 1}</span>
              <Btn onClick={() => removeRule(rule.id)} disabled={rules.length === 1}>Remove</Btn>
            </div>

            <Field label="Condition type">
              <select className={inputClassName} value={rule.scopeKind} onChange={(event) => updateRule(rule.id, { scopeKind: event.target.value as SimpleScopeKind })}>
                {SIMPLE_SCOPE_KINDS.map((value) => <option key={value} value={value}>{value}</option>)}
              </select>
            </Field>

            <div className="mt-3">
              <ContentRuleScopeFields scopeKind={rule.scopeKind} draft={rule} onChange={(update) => updateRule(rule.id, update)} />
            </div>
          </div>
        ))}
      </div>

      <Btn onClick={() => onRulesChange([...rules, createExpressionRuleDraft("tag")])}>+ Add condition</Btn>
    </div>
  );
}

function CreateContentRuleDialog({ roles, onClose }: { roles: RoleRow[]; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [mode, setMode] = useState<"rule" | "override">("rule");
  const [roleId, setRoleId] = useState(roles[0]?.id ?? 0);
  const [entityKind, setEntityKind] = useState<(typeof ENTITY_KINDS)[number]>("video");
  const [effect, setEffect] = useState<(typeof EFFECTS)[number]>("deny");
  const [scopeKind, setScopeKind] = useState<(typeof SCOPE_KINDS)[number]>("all");
  const [scopeDraft, setScopeDraft] = useState<ContentRuleScopeDraft>(() => createEmptyScopeDraft());
  const [expressionOperator, setExpressionOperator] = useState<ExpressionOperator>("and");
  const [expressionRules, setExpressionRules] = useState<ContentRuleExpressionRuleDraft[]>(() => [createExpressionRuleDraft("tag")]);
  const [appliesTo, setAppliesTo] = useState<(typeof APPLIES_TO)[number]>("all");
  const [entityId, setEntityId] = useState("");
  const [error, setError] = useState<string | null>(null);
  const builtScope = useMemo(
    () => buildScopeValue(scopeKind, scopeDraft, expressionOperator, expressionRules),
    [expressionOperator, expressionRules, scopeDraft, scopeKind],
  );

  const createRule = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => {
      if (!builtScope.ok) {
        throw new Error(builtScope.error);
      }

      return contentRulesApi.create({ roleId, entityKind, effect, scopeKind, scopeValue: JSON.stringify(builtScope.value), appliesTo });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "content-rules"] });
      onClose();
    },
    onError: (err: Error) => setError(err.message),
  });
  const createOverride = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => contentRulesApi.createOverride({ roleId, entityKind, entityId, effect, appliesTo }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["admin", "entity-overrides"] });
      onClose();
    },
    onError: (err: Error) => setError(err.message),
  });

  return (
    <Modal title="Create content rule" onClose={onClose}>
      <div className="space-y-3">
        <div className="flex gap-2 border-b border-border pb-3">
          <Btn onClick={() => setMode("rule")} className={mode === "rule" ? "border-accent bg-accent/10 text-foreground" : ""}>Rule</Btn>
          <Btn onClick={() => setMode("override")} className={mode === "override" ? "border-accent bg-accent/10 text-foreground" : ""}>Override</Btn>
        </div>

        <Field label="Role">
          <select className={inputClassName} value={roleId} onChange={(event) => setRoleId(Number(event.target.value))}>
            {roles.map((role) => <option key={role.id} value={role.id}>{role.name}</option>)}
          </select>
        </Field>

        <Field label="Entity kind">
          <select className={inputClassName} value={entityKind} onChange={(event) => setEntityKind(event.target.value as (typeof ENTITY_KINDS)[number])}>
            {ENTITY_KINDS.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
          </select>
        </Field>

        <div className="grid gap-3 md:grid-cols-2">
          <Field label="Effect">
            <select className={inputClassName} value={effect} onChange={(event) => setEffect(event.target.value as (typeof EFFECTS)[number])}>
              {EFFECTS.map((value) => <option key={value} value={value}>{value}</option>)}
            </select>
          </Field>
          <Field label="Applies to">
            <select className={inputClassName} value={appliesTo} onChange={(event) => setAppliesTo(event.target.value as (typeof APPLIES_TO)[number])}>
              {APPLIES_TO.map((value) => <option key={value} value={value}>{value}</option>)}
            </select>
          </Field>
        </div>

        {mode === "rule" ? (
          <>
            <Field label="Scope kind">
              <select className={inputClassName} value={scopeKind} onChange={(event) => setScopeKind(event.target.value as (typeof SCOPE_KINDS)[number])}>
                {SCOPE_KINDS.map((value) => <option key={value} value={value}>{value}</option>)}
              </select>
            </Field>

            {scopeKind === "expression" ? (
              <ExpressionRuleBuilder
                operator={expressionOperator}
                rules={expressionRules}
                onOperatorChange={setExpressionOperator}
                onRulesChange={setExpressionRules}
              />
            ) : (
              <ContentRuleScopeFields scopeKind={scopeKind} draft={scopeDraft} onChange={(update) => setScopeDraft((current) => ({ ...current, ...update }))} />
            )}

            {builtScope.ok ? (
              <p className="text-xs text-secondary">Summary: {formatParsedScopeSummary(scopeKind, builtScope.value)}</p>
            ) : null}
          </>
        ) : (
          <Field label="Entity id">
            <input className={inputClassName} value={entityId} onChange={(event) => setEntityId(event.target.value)} placeholder="123" />
          </Field>
        )}

        {mode === "rule" && !builtScope.ok ? <p className="text-sm text-red-400">{builtScope.error}</p> : null}
        {error ? <p className="text-sm text-red-400">{error}</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={onClose}>Cancel</Btn>
          <Btn
            variant="primary"
            onClick={() => mode === "rule" ? createRule.mutate() : createOverride.mutate()}
            disabled={createRule.isPending || createOverride.isPending || (mode === "override" && !entityId.trim()) || (mode === "rule" && !builtScope.ok)}
          >
            Create
          </Btn>
        </div>
      </div>
    </Modal>
  );
}

function ApiTokensTable({ tokens, onRevoke }: { tokens: ApiTokenRow[]; onRevoke: (id: string) => void }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
          <tr>
            <th className="px-2 py-2">Name</th>
            <th className="px-2 py-2">Prefix</th>
            <th className="px-2 py-2">Scope</th>
            <th className="px-2 py-2">Created</th>
            <th className="px-2 py-2">Last used</th>
            <th className="px-2 py-2">Expires</th>
            <th className="px-2 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {tokens.map((token) => (
            <tr key={token.id} className="border-b border-border/40">
              <td className="px-2 py-2 font-medium">{token.name}</td>
              <td className="px-2 py-2 font-mono text-xs">{token.prefix}…</td>
              <td className="px-2 py-2 text-secondary">{token.scope?.length ? `${token.scope.length} perms` : "full"}</td>
              <td className="px-2 py-2 text-secondary">{formatDateTime(token.createdAt)}</td>
              <td className="px-2 py-2 text-secondary">{token.lastUsedAt ? formatDateTime(token.lastUsedAt) : "—"}</td>
              <td className="px-2 py-2 text-secondary">{token.expiresAt ? formatDateTime(token.expiresAt) : "never"}</td>
              <td className="px-2 py-2 text-right">
                <Btn variant="danger" onClick={() => { if (confirm(`Revoke token "${token.name}"?`)) onRevoke(token.id); }}>Revoke</Btn>
              </td>
            </tr>
          ))}
          {tokens.length === 0 ? (
            <tr>
              <td colSpan={7} className="px-2 py-4 text-center text-secondary">No API tokens.</td>
            </tr>
          ) : null}
        </tbody>
      </table>
    </div>
  );
}

function CreateApiTokenDialog({ onClose, onIssued }: { onClose: () => void; onIssued: (token: ApiTokenIssuedRow) => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [expires, setExpires] = useState("");
  const [error, setError] = useState<string | null>(null);

  const create = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => apiTokensApi.create({ name, expiresAt: expires || undefined }),
    onSuccess: (token) => {
      queryClient.invalidateQueries({ queryKey: ["admin", "api-tokens"] });
      onIssued(token);
    },
    onError: (err: Error) => setError(err.message),
  });

  return (
    <Modal title="Create API token" onClose={onClose}>
      <div className="space-y-3">
        <Field label="Name"><input className={inputClassName} value={name} onChange={(event) => setName(event.target.value)} placeholder="my-laptop" /></Field>
        <Field label="Expires (ISO datetime, blank = never)"><input className={inputClassName} value={expires} onChange={(event) => setExpires(event.target.value)} placeholder="2026-12-31T00:00:00Z" /></Field>
        {error ? <p className="text-sm text-red-400">{error}</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={onClose}>Cancel</Btn>
          <Btn variant="primary" onClick={() => create.mutate()} disabled={!name || create.isPending}>Create</Btn>
        </div>
      </div>
    </Modal>
  );
}

function IssuedTokenDialog({ token, onClose }: { token: ApiTokenIssuedRow; onClose: () => void }) {
  return (
    <Modal title="Token issued" onClose={onClose}>
      <div className="space-y-3">
        <p className="text-sm text-amber-400">Copy this token now. You will not be shown it again.</p>
        <pre className="overflow-auto rounded-xl border border-border bg-card p-3 text-xs text-foreground">{token.plaintextToken}</pre>
        <div className="flex justify-end gap-2 pt-2">
          <Btn variant="primary" onClick={() => { navigator.clipboard.writeText(token.plaintextToken); onClose(); }}>Copy & close</Btn>
        </div>
      </div>
    </Modal>
  );
}

function ShareLinksTable({ links, onRevoke }: { links: ShareLinkRow[]; onRevoke: (id: string) => void }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="border-b border-border text-left text-xs uppercase tracking-wide text-secondary">
          <tr>
            <th className="px-2 py-2">Created by</th>
            <th className="px-2 py-2">Entity</th>
            <th className="px-2 py-2">Ids</th>
            <th className="px-2 py-2">Views</th>
            <th className="px-2 py-2">Expires</th>
            <th className="px-2 py-2">Status</th>
            <th className="px-2 py-2 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {links.map((link) => (
            <tr key={link.id} className="border-b border-border/40">
              <td className="px-2 py-2">{link.createdByUsername ?? <span className="text-secondary">—</span>}</td>
              <td className="px-2 py-2">{formatEntityKind(link.entityKind)}</td>
              <td className="px-2 py-2 text-secondary">{link.entityIds.length}</td>
              <td className="px-2 py-2 text-secondary">{link.viewCount}</td>
              <td className="px-2 py-2 text-secondary">{link.expiresAt ? formatDateTime(link.expiresAt) : "never"}</td>
              <td className="px-2 py-2">
                {link.revoked ? <span className="text-red-400">revoked</span> : link.hasPassword ? "password-gated" : "active"}
              </td>
              <td className="px-2 py-2 text-right">
                {!link.revoked ? <Btn variant="danger" onClick={() => { if (confirm("Revoke this share link?")) onRevoke(link.id); }}>Revoke</Btn> : null}
              </td>
            </tr>
          ))}
          {links.length === 0 ? (
            <tr>
              <td colSpan={7} className="px-2 py-4 text-center text-secondary">No share links.</td>
            </tr>
          ) : null}
        </tbody>
      </table>
    </div>
  );
}

function CreateShareLinkDialog({ onClose, onIssued }: { onClose: () => void; onIssued: (link: ShareLinkIssuedRow) => void }) {
  const queryClient = useQueryClient();
  const [entityKind, setEntityKind] = useState<(typeof ENTITY_KINDS)[number]>("video");
  const [ids, setIds] = useState("");
  const [expires, setExpires] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  const create = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => shareLinksApi.create({
      entityKind,
      entityIds: ids.split(/[\s,]+/).map((value) => value.trim()).filter(Boolean),
      expiresAt: expires || undefined,
      password: password || undefined,
    }),
    onSuccess: (link) => {
      queryClient.invalidateQueries({ queryKey: ["admin", "share-links"] });
      onIssued(link);
    },
    onError: (err: Error) => setError(err.message),
  });

  return (
    <Modal title="Create share link" onClose={onClose}>
      <div className="space-y-3">
        <Field label="Entity kind">
          <select className={inputClassName} value={entityKind} onChange={(event) => setEntityKind(event.target.value as (typeof ENTITY_KINDS)[number])}>
            {ENTITY_KINDS.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
          </select>
        </Field>
        <Field label="Entity ids (comma- or space-separated)">
          <input className={inputClassName} value={ids} onChange={(event) => setIds(event.target.value)} placeholder="123, 124, 125" />
        </Field>
        <Field label="Expires (ISO datetime, blank = never)">
          <input className={inputClassName} value={expires} onChange={(event) => setExpires(event.target.value)} />
        </Field>
        <Field label="Password (optional)">
          <input className={inputClassName} type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
        </Field>
        {error ? <p className="text-sm text-red-400">{error}</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={onClose}>Cancel</Btn>
          <Btn variant="primary" onClick={() => create.mutate()} disabled={!ids.trim() || create.isPending}>Create</Btn>
        </div>
      </div>
    </Modal>
  );
}

function IssuedShareLinkDialog({ link, onClose }: { link: ShareLinkIssuedRow; onClose: () => void }) {
  const primaryEntityId = link.entityIds.length === 1 ? Number(link.entityIds[0]) : undefined;
  const routeEntityKind = link.entityKind;
  const canUseDetailRoute = link.entityIds.length === 1 && Number.isInteger(primaryEntityId) && (primaryEntityId ?? 0) > 0;
  const routePath = buildRoutePath(canUseDetailRoute
    ? { page: routeEntityKind, id: primaryEntityId }
    : { page: ENTITY_LIST_ROUTES[link.entityKind] ?? routeEntityKind });
  const shareUrl = new URL(routePath, window.location.origin);
  shareUrl.searchParams.set("share_token", link.plaintextToken);

  return (
    <Modal title="Share link issued" onClose={onClose}>
      <div className="space-y-3">
        <p className="text-sm text-amber-400">Copy this link now. The plaintext share token will not be shown again.</p>
        <pre className="overflow-auto rounded-xl border border-border bg-card p-3 text-xs text-foreground">{shareUrl.toString()}</pre>
        {link.hasPassword ? <p className="text-xs text-secondary">This link is password-gated. Share the password separately; the recipient will be prompted for it.</p> : null}
        <div className="flex justify-end gap-2 pt-2">
          <Btn onClick={() => navigator.clipboard.writeText(link.plaintextToken)}>Copy raw token</Btn>
          <Btn variant="primary" onClick={() => { navigator.clipboard.writeText(shareUrl.toString()); onClose(); }}>Copy link & close</Btn>
        </div>
      </div>
    </Modal>
  );
}

// =========================================================================
// shared
// =========================================================================
function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  return (
    <EditModal title={title} open onClose={onClose} maxWidthClassName={wide ? "sm:max-w-3xl" : "sm:max-w-lg"}>
      {children}
    </EditModal>
  );
}
