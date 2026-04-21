interface StringListEditorProps {
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  addLabel?: string;
  inputType?: "text" | "url";
}

export function StringListEditor({
  values,
  onChange,
  placeholder,
  addLabel = "Add entry",
  inputType = "text",
}: StringListEditorProps) {
  const renderedValues = values.length > 0 ? values : [""];

  const updateValue = (index: number, nextValue: string) => {
    onChange(renderedValues.map((value, valueIndex) => (valueIndex === index ? nextValue : value)));
  };

  const removeValue = (index: number) => {
    const nextValues = renderedValues.filter((_, valueIndex) => valueIndex !== index);
    onChange(nextValues.length > 0 ? nextValues : [""]);
  };

  const addValue = () => {
    onChange([...renderedValues, ""]);
  };

  return (
    <div>
      <div className="space-y-1.5">
        {renderedValues.map((value, index) => (
          <div key={`${index}-${value}`} className="flex items-center gap-1.5">
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