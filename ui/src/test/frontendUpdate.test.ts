import { showFrontendUpdate } from "../frontendUpdate";

it("blocks existing capture shortcuts, cancellation, and duplicate dialogs until the user refreshes", () => {
  HTMLDialogElement.prototype.showModal = function () {
    this.open = true;
  };
  const shortcut = vi.fn();
  window.addEventListener("keydown", shortcut, { capture: true });
  window.dispatchEvent(new KeyboardEvent("keydown", { key: "x" }));
  expect(shortcut).toHaveBeenCalledTimes(1);
  const reload = vi.fn();
  const url = window.location.href;
  localStorage.setItem("update-test-preference", "kept");
  showFrontendUpdate(reload);
  showFrontendUpdate(reload);
  const dialog = document.querySelector("dialog")!;
  expect(document.querySelectorAll("dialog")).toHaveLength(1);
  expect(dialog.open).toBe(true);
  expect(dialog.textContent).toContain("interrupts playback and discards unsaved edits");
  const cancel = new Event("cancel", { cancelable: true });
  dialog.dispatchEvent(cancel);
  expect(cancel.defaultPrevented).toBe(true);
  const key = new KeyboardEvent("keydown", { key: "Escape", cancelable: true });
  window.dispatchEvent(key);
  expect(key.defaultPrevented).toBe(true);
  expect(shortcut).toHaveBeenCalledTimes(1);
  expect(reload).not.toHaveBeenCalled();
  expect(document.activeElement).toBe(dialog.querySelector("button"));
  dialog.querySelector("button")!.click();
  expect(reload).toHaveBeenCalledOnce();
  expect(window.location.href).toBe(url);
  expect(localStorage.getItem("update-test-preference")).toBe("kept");
  window.removeEventListener("keydown", shortcut, { capture: true });
});
