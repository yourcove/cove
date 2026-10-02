import { useState } from "react";

interface StringListEditorProps {
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  addLabel?: string;
  inputType?: "text" | "url";
}

// Keys only need to be unique among the current entries, so a new key is one past the largest in use.
function withFreshKeys(keys: number[], count: number) {
  const next = keys.slice(0, count);
  let nextKey = Math.max(0, ...next) + 1;
  while (next.length < count) next.push(nextKey++);
  return next;
}

export function StringListEditor({
  values,
  onChange,
  placeholder,
  addLabel = "Add entry",
  inputType = "text",
}: StringListEditorProps) {
  const renderedValues = values.length > 0 ? values : [""];

  // Maintain stable keys for each entry so React preserves focus across re-renders. When the entry
  // count changes from outside, the keys follow it during render.
  const [keys, setKeys] = useState(() => withFreshKeys([], renderedValues.length));
  if (keys.length !== renderedValues.length) {
    setKeys(withFreshKeys(keys, renderedValues.length));
  }

  const updateValue = (index: number, nextValue: string) => {
    onChange(renderedValues.map((value, valueIndex) => (valueIndex === index ? nextValue : value)));
  };

  const removeValue = (index: number) => {
    const nextValues = renderedValues.filter((_, valueIndex) => valueIndex !== index);
    setKeys(keys.filter((_, keyIndex) => keyIndex !== index));
    onChange(nextValues.length > 0 ? nextValues : [""]);
  };

  const addValue = () => {
    setKeys(withFreshKeys(keys, keys.length + 1));
    onChange([...renderedValues, ""]);
  };

  return (
    <div>
      <div className="space-y-1.5">
        {renderedValues.map((value, index) => (
          <div key={keys[index]} className="flex items-center gap-1.5">
            <input
              type={inputType}
              value={value}
              onChange={(event) => updateValue(index, event.target.value)}
              placeholder={placeholder}
              className="flex-1 bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent"
            />
            <button
              type="button"
              onClick={() => removeValue(index)}
              className="p-1 text-muted hover:text-red-400 transition-colors flex-shrink-0"
              title="Remove entry"
            >
              ×
            </button>
          </div>
        ))}
      </div>
      <button
        type="button"
        onClick={addValue}
        className="mt-1.5 flex items-center gap-1 text-xs text-accent hover:text-accent-hover"
      >
        + {addLabel}
      </button>
    </div>
  );
}
