import type { Command, Option } from "commander";
import { CliError } from "./errors";

export type CompletionShell = "bash" | "zsh" | "fish";

interface CompletionOption {
  short?: string;
  long?: string;
  description: string;
  takesValue: boolean;
  choices?: string[];
}

interface CompletionCandidate {
  value: string;
  description: string;
}

interface CompletionTransition {
  from: string;
  token: string;
  to: string;
}

interface CompletionNode {
  path: string;
  candidates: CompletionCandidate[];
  options: CompletionOption[];
}

interface CompletionModel {
  nodes: CompletionNode[];
  transitions: CompletionTransition[];
  valueOptions: CompletionOption[];
}

const OUTPUT_FORMATS = ["human", "json", "jsonl"];
const HYPERLINK_MODES = ["auto", "always", "never"];
const OPTION_CHOICES: Record<string, string[]> = {
  "--hyperlinks": HYPERLINK_MODES,
  "--output": OUTPUT_FORMATS,
};
const optionCompletionChoices = new WeakMap<Option, readonly string[]>();
const COMPLETION_SHELLS: CompletionShell[] = ["bash", "zsh", "fish"];

export function setCompletionChoices(option: Option, choices: readonly string[]): Option {
  optionCompletionChoices.set(option, choices);
  return option;
}

export function parseCompletionShell(value: string): CompletionShell {
  if (value === "bash" || value === "zsh" || value === "fish") return value;
  throw new CliError("INVALID_ARGUMENT", "Shell must be bash, zsh, or fish.");
}

export function renderCompletion(root: Command, shell: CompletionShell): string {
  const model = completionModel(root);
  if (shell === "bash") return renderBash(model);
  if (shell === "zsh") return renderZsh(model);
  return renderFish(model);
}

function completionModel(root: Command): CompletionModel {
  const nodes: CompletionNode[] = [];
  const transitions: CompletionTransition[] = [];
  const valueOptions = new Map<string, CompletionOption>();

  function visit(command: Command, path: string[]): void {
    const help = command.createHelp();
    const commandOptions = [
      ...help.visibleOptions(command),
      ...(command === root ? [] : help.visibleGlobalOptions(command)),
    ].map(completionOption);
    const options = uniqueOptions(commandOptions);
    for (const option of options.filter(item => item.takesValue)) {
      for (const flag of [option.short, option.long].filter((value): value is string => !!value)) valueOptions.set(flag, option);
    }

    const children = help.visibleCommands(command);
    const candidates: CompletionCandidate[] = children.map(child => ({
      value: child.name(),
      description: oneLine(child.description()) || `Use ${child.name()}`,
    }));
    for (const option of options) {
      for (const flag of [option.short, option.long].filter((value): value is string => !!value)) {
        candidates.push({ value: flag, description: option.description });
      }
    }
    if (path.join(" ") === "completion") {
      candidates.push(...COMPLETION_SHELLS.map(value => ({ value, description: `Generate ${value} completions` })));
    }
    nodes.push({ path: path.join(" "), candidates: uniqueCandidates(candidates), options });

    for (const child of children) {
      const childPath = [...path, child.name()].join(" ");
      for (const token of [child.name(), ...child.aliases()]) transitions.push({ from: path.join(" "), token, to: childPath });
      visit(child, [...path, child.name()]);
    }
  }

  visit(root, []);
  return { nodes, transitions, valueOptions: uniqueOptions([...valueOptions.values()]) };
}

function completionOption(option: Option): CompletionOption {
  return {
    short: option.short,
    long: option.long,
    description: oneLine(option.description) || "Command option",
    takesValue: option.required || option.optional,
    ...(optionCompletionChoices.get(option) ? { choices: [...optionCompletionChoices.get(option)!] }
      : option.long && OPTION_CHOICES[option.long] ? { choices: OPTION_CHOICES[option.long] }
      : {}),
  };
}

function uniqueOptions(options: CompletionOption[]): CompletionOption[] {
  const seen = new Set<string>();
  return options.filter(option => {
    const key = `${option.short ?? ""}|${option.long ?? ""}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function uniqueCandidates(candidates: CompletionCandidate[]): CompletionCandidate[] {
  const seen = new Set<string>();
  return candidates.filter(candidate => {
    if (seen.has(candidate.value)) return false;
    seen.add(candidate.value);
    return true;
  });
}

function oneLine(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

function shellQuote(value: string): string {
  return `'${value.replace(/'/g, `'\\''`)}'`;
}

function fishQuote(value: string): string {
  return `'${value.replace(/\\/g, "\\\\").replace(/'/g, "\\'")}'`;
}

function fishDoubleQuote(value: string): string {
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\$/g, "\\$")}"`;
}

function optionFlags(options: CompletionOption[]): string[] {
  return options.flatMap(option => [option.short, option.long]).filter((value): value is string => !!value);
}

function bashCandidates(node: CompletionNode): string {
  return node.candidates.map(candidate => candidate.value).join(" ");
}

function choiceFlags(model: CompletionModel): Set<string> {
  return new Set(model.nodes.flatMap(node => node.options.filter(option => option.choices?.length).flatMap(option => optionFlags([option]))));
}

function renderBash(model: CompletionModel): string {
  const choices = choiceFlags(model);
  const globalChoiceOptions = model.valueOptions.filter(option => option.long !== undefined && OPTION_CHOICES[option.long] !== undefined);
  const otherValueOptions = model.valueOptions.filter(option => !optionFlags([option]).some(flag => choices.has(flag)));
  const otherValueFlags = optionFlags(otherValueOptions);
  const otherAttachedPatterns = otherValueOptions
    .flatMap(option => [
      ...(option.long ? [`${option.long}=*`] : []),
      ...(option.short ? [`${option.short}?*`] : []),
    ]);
  const attachedPatterns = model.valueOptions.flatMap(option => [
    ...(option.long ? [`${option.long}=*`] : []),
    ...(option.short ? [`${option.short}?*`] : []),
  ]);
  const splitChoiceCases = globalChoiceOptions.map(option => `      ${[option.short, option.long].filter(Boolean).join("|")})
        COMPREPLY=( $(compgen -W ${shellQuote(option.choices!.join(" "))} -- "$cur") )
        return
        ;;`).join("\n");
  const attachedChoiceCases = model.nodes.flatMap(node => node.options.filter(option => option.choices?.length).flatMap(option => [
    ...(option.long ? [`    ${shellQuote(`${node.path}:${option.long}=`)}*)
      COMPREPLY=( $(compgen -W ${shellQuote(option.choices!.map(value => `${option.long}=${value}`).join(" "))} -- "$cur") )
      ${option.long === "--filter-by" ? "__cove_cli_finish_filter_by \"$cur\" \"$raw_cur\"" : ""}
      return
      ;;`] : []),
    ...(option.short ? [`    ${shellQuote(`${node.path}:${option.short}=`)}*)
      COMPREPLY=( $(compgen -W ${shellQuote(option.choices!.map(value => `${option.short}=${value}`).join(" "))} -- "$cur") )
      return
      ;;
    ${shellQuote(`${node.path}:${option.short}`)}?*)
      COMPREPLY=( $(compgen -W ${shellQuote(option.choices!.map(value => `${option.short}${value}`).join(" "))} -- "$cur") )
      return
      ;;`] : []),
  ])).join("\n");
  const previousChoiceCases = model.nodes.flatMap(node => node.options.filter(option => option.choices?.length).map(option => `      ${[option.short, option.long].filter(Boolean).map(flag => shellQuote(`${node.path}:${flag}`)).join("|")})
        COMPREPLY=( $(compgen -W ${shellQuote(option.choices!.join(" "))} -- "$cur") )
        ${option.long === "--filter-by" ? "__cove_cli_finish_filter_by \"$cur\" \"$raw_cur\"" : ""}
        return
        ;;`)).join("\n");
  return `# bash completion for cove-cli
__cove_cli_split_words() {
  local line="\${COMP_LINE:0:COMP_POINT}"
  local token=""
  local quote=""
  local character
  local escaped=0
  local started=0
  local index
  __cove_cli_words=()

  for (( index=0; index<\${#line}; index++ )); do
    character="\${line:index:1}"
    if (( escaped )); then
      token+="$character"
      started=1
      escaped=0
      continue
    fi
    if [[ "$quote" == "'" ]]; then
      if [[ "$character" == "'" ]]; then
        quote=""
      else
        token+="$character"
      fi
      continue
    fi
    if [[ "$quote" == '"' ]]; then
      if [[ "$character" == '"' ]]; then
        quote=""
      elif [[ "$character" == "\\\\" ]]; then
        escaped=1
      else
        token+="$character"
      fi
      continue
    fi
    if [[ "$character" == "\\\\" ]]; then
      escaped=1
      started=1
    elif [[ "$character" == "'" || "$character" == '"' ]]; then
      quote="$character"
      started=1
    elif [[ "$character" == " " || "$character" == $'\\t' || "$character" == $'\\n' ]]; then
      if (( started )); then
        __cove_cli_words+=("$token")
        token=""
        started=0
      fi
    else
      token+="$character"
      started=1
    fi
  done
  __cove_cli_words+=("$token")
}

__cove_cli_finish_filter_by() {
  local full_cur="$1"
  local raw_cur="$2"
  local prefix="\${full_cur%"$raw_cur"}"
  local index
  if [[ -n "$prefix" ]]; then
    for (( index=0; index<\${#COMPREPLY[@]}; index++ )); do
      COMPREPLY[index]="\${COMPREPLY[index]#"$prefix"}"
    done
  fi
  (( \${#COMPREPLY[@]} > 0 )) || return
  local reply
  for reply in "\${COMPREPLY[@]}"; do
    [[ "$reply" == *= ]] || return
  done
  compopt -o nospace
}

_cove_cli() {
  local cur="\${COMP_WORDS[COMP_CWORD]}"
  local raw_cur="$cur"

  if (( COMP_CWORD > 1 )) && [[ "\${COMP_WORDS[COMP_CWORD-1]}" == "=" ]]; then
    case "\${COMP_WORDS[COMP_CWORD-2]}" in
${splitChoiceCases}
      ${otherValueFlags.join("|")})
        COMPREPLY=()
        return
        ;;
    esac
  fi

  __cove_cli_split_words
  local -a words=("\${__cove_cli_words[@]}")
  local cword=$((\${#words[@]} - 1))
  cur="\${words[cword]}"

  local path=""
  local expect_value=0
  local word
  local i
  for (( i=1; i<cword; i++ )); do
    word="\${words[i]}"
    if (( expect_value )); then
      expect_value=0
      continue
    fi
    case "$word" in
      ${attachedPatterns.join("|")}) continue ;;
      ${optionFlags(model.valueOptions).join("|")}) expect_value=1; continue ;;
      --) COMPREPLY=(); return ;;
      -*) continue ;;
    esac
    case "$path:$word" in
${model.transitions.map(transition => `      ${shellQuote(`${transition.from}:${transition.token}`)}) path=${shellQuote(transition.to)} ;;`).join("\n")}
    esac
  done

  case "$path:$cur" in
${attachedChoiceCases}
  esac

  case "$cur" in
    ${otherAttachedPatterns.join("|")})
      COMPREPLY=()
      return
      ;;
  esac

  if (( cword > 0 )); then
    case "$path:\${words[cword-1]}" in
${previousChoiceCases}
    esac
    case "\${words[cword-1]}" in
      ${otherValueFlags.join("|")})
        COMPREPLY=()
        return
        ;;
    esac
  fi

  local candidates=""
  case "$path" in
${model.nodes.map(node => `    ${shellQuote(node.path)}) candidates=${shellQuote(bashCandidates(node))} ;;`).join("\n")}
  esac
  COMPREPLY=( $(compgen -W "$candidates" -- "$cur") )
}
complete -F _cove_cli cove-cli
`;
}

function zshCandidates(node: CompletionNode): string {
  return node.candidates.map(candidate => `      ${shellQuote(`${candidate.value}:${candidate.description}`)}`).join("\n");
}

function zshChoiceCommands(option: CompletionOption, prefix: string, attached: boolean, indent: string): string {
  const commands = (values: string[], suffix: string): string => {
    if (!values.length) return "";
    const candidates = values.map(value => shellQuote(`${prefix}${value}`)).join(" ");
    if (!attached) return `${indent}compadd ${suffix}-- ${candidates}`;
    return `${indent}attached_choices=(${candidates})\n${indent}compadd ${suffix}-- \${(M)attached_choices:#\${cur}*}`;
  };
  if (option.long !== "--filter-by") return commands(option.choices!, "");
  return [
    commands(option.choices!.filter(value => value.endsWith("=")), "-S '' "),
    commands(option.choices!.filter(value => !value.endsWith("=")), ""),
  ].filter(Boolean).join("\n");
}

function renderZsh(model: CompletionModel): string {
  const choices = choiceFlags(model);
  const otherValueFlags = optionFlags(model.valueOptions.filter(option => !optionFlags([option]).some(flag => choices.has(flag))));
  const attachedPatterns = model.valueOptions.flatMap(option => [
    ...(option.long ? [`${option.long}=*`] : []),
    ...(option.short ? [`${option.short}?*`] : []),
  ]);
  const attachedChoiceCases = model.nodes.flatMap(node => node.options.filter(option => option.choices?.length).flatMap(option => [
    ...(option.long ? [`    ${shellQuote(`${node.path}:${option.long}=`)}*)
${zshChoiceCommands(option, `${option.long}=`, true, "      ")}
      return
      ;;`] : []),
    ...(option.short ? [`    ${shellQuote(`${node.path}:${option.short}=`)}*)
${zshChoiceCommands(option, `${option.short}=`, true, "      ")}
      return
      ;;
    ${shellQuote(`${node.path}:${option.short}`)}?*)
${zshChoiceCommands(option, option.short, true, "      ")}
      return
      ;;`] : []),
  ])).join("\n");
  const previousChoiceCases = model.nodes.flatMap(node => node.options.filter(option => option.choices?.length).map(option => `      ${[option.short, option.long].filter(Boolean).map(flag => shellQuote(`${node.path}:${flag}`)).join("|")})
${zshChoiceCommands(option, "", false, "        ")}
        return
        ;;`)).join("\n");
  return `#compdef cove-cli
# zsh completion for cove-cli
_cove_cli() {
  local cur="\${words[CURRENT]}"
  local -a attached_choices
  local path=""
  local word
  integer expect_value=0
  integer i
  for (( i=2; i<CURRENT; i++ )); do
    word="\${words[i]}"
    if (( expect_value )); then
      expect_value=0
      continue
    fi
    case "$word" in
      ${attachedPatterns.join("|")}) continue ;;
      ${optionFlags(model.valueOptions).join("|")}) expect_value=1; continue ;;
      --) return 0 ;;
      -*) continue ;;
    esac
    case "$path:$word" in
${model.transitions.map(transition => `      ${shellQuote(`${transition.from}:${transition.token}`)}) path=${shellQuote(transition.to)} ;;`).join("\n")}
    esac
  done

  case "$path:$cur" in
${attachedChoiceCases}
  esac
  if (( CURRENT > 1 )); then
    case "$path:\${words[CURRENT-1]}" in
${previousChoiceCases}
    esac
    case "\${words[CURRENT-1]}" in
      ${otherValueFlags.join("|")})
        _message 'value'
        return
        ;;
    esac
  fi

  local -a candidates
  case "$path" in
${model.nodes.map(node => `    ${shellQuote(node.path)})
      candidates=(
${zshCandidates(node)}
      )
      ;;`).join("\n")}
  esac
  _describe 'cove-cli' candidates
}
compdef _cove_cli cove-cli
`;
}

function fishPath(path: string): string {
  return path || "__root__";
}

function fishCondition(path: string): string {
  return fishDoubleQuote(`__cove_cli_path_is ${fishQuote(fishPath(path))}`);
}

function fishOption(option: CompletionOption): string {
  const flags = [
    ...(option.short ? ["-s", fishQuote(option.short.slice(1))] : []),
    ...(option.long ? ["-l", fishQuote(option.long.slice(2))] : []),
  ];
  if (option.takesValue) flags.push("-r", "-f");
  if (option.choices) flags.push("-a", fishQuote(option.choices.join(" ")));
  flags.push("-d", fishQuote(option.description));
  return flags.join(" ");
}

function renderFish(model: CompletionModel): string {
  const attachedPatterns = model.valueOptions.flatMap(option => [
    ...(option.long ? [`${option.long}=*`] : []),
    ...(option.short ? [`${option.short}?*`] : []),
  ]);
  const transitionCases = model.transitions.map(transition =>
    `      case ${fishQuote(`${fishPath(transition.from)}:${transition.token}`)}\n        set path ${fishQuote(fishPath(transition.to))}`).join("\n");
  const completions: string[] = [];
  for (const node of model.nodes) {
    const condition = fishCondition(node.path);
    for (const candidate of node.candidates.filter(candidate => !candidate.value.startsWith("-"))) {
      completions.push(`complete -c cove-cli -f -n ${condition} -a ${fishQuote(candidate.value)} -d ${fishQuote(candidate.description)}`);
    }
    for (const option of node.options) completions.push(`complete -c cove-cli -n ${condition} ${fishOption(option)}`);
  }
  return `# fish completion for cove-cli
function __cove_cli_path
  set -l tokens (commandline -opc)
  if test (count $tokens) -gt 0
    set -e tokens[1]
  end
  set -l path '__root__'
  set -l expect_value 0
  for token in $tokens
    if test $expect_value -eq 1
      set expect_value 0
      continue
    end
    if test "$token" = '--'
      echo '__stop__'
      return
    end
    switch $token
      case ${attachedPatterns.map(fishQuote).join(" ")}
        continue
      case ${optionFlags(model.valueOptions).map(fishQuote).join(" ")}
        set expect_value 1
        continue
      case '-*'
        continue
    end
    switch "$path:$token"
${transitionCases}
    end
  end
  echo $path
end

function __cove_cli_path_is
  set -l current (__cove_cli_path)
  test "$current" = "$argv[1]"
end

complete -c cove-cli -e
${completions.join("\n")}
`;
}
