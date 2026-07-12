import { useRef, type InputHTMLAttributes } from "react";
import { CalendarDays } from "lucide-react";

type IsoDateInputProps = InputHTMLAttributes<HTMLInputElement> & {
  pickerType?: "date" | "datetime-local";
};

export function IsoDateInput({ pickerType = "date", className = "", value, onChange, disabled, type: _type, inputMode: _inputMode, placeholder: _placeholder, ...props }: IsoDateInputProps) {
  const pickerRef = useRef<HTMLInputElement>(null);
  const placeholder = pickerType === "date" ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm";

  const openPicker = () => {
    const picker = pickerRef.current;
    if (!picker) return;
    picker.value = typeof value === "string" ? value : "";
    if (typeof picker.showPicker === "function") picker.showPicker();
    else picker.click();
  };

  return (
    <span className="relative block w-full">
      <input
        {...props}
        type="text"
        inputMode="numeric"
        placeholder={placeholder}
        value={value}
        onChange={onChange}
        disabled={disabled}
        className={`${className} w-full pr-10`.trim()}
      />
      <button
        type="button"
        aria-label="Choose date"
        title="Choose date"
        disabled={disabled}
        onClick={openPicker}
        className="absolute inset-y-0 right-0 flex w-10 items-center justify-center text-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
      >
        <CalendarDays className="h-4 w-4" />
      </button>
      <input
        ref={pickerRef}
        type={pickerType}
        tabIndex={-1}
        aria-hidden="true"
        className="pointer-events-none absolute h-px w-px opacity-0"
        onChange={onChange}
      />
    </span>
  );
}
