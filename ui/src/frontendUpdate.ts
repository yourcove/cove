let blocking = false;
// Register before the application is imported: its own capture-phase shortcuts
// must not run first when an update is detected later.
for (const type of ["keydown", "keyup", "keypress"]) {
  window.addEventListener(
    type,
    (event) => {
      if (!blocking) return;
      event.stopImmediatePropagation();
      if ((event as KeyboardEvent).key === "Escape") event.preventDefault();
    },
    { capture: true },
  );
}

// Independent of React, routing, and app CSS so this also covers login and
// application bootstrap failures. Native modal dialogs make the app inert.
export function showFrontendUpdate(reload = () => window.location.reload()) {
  if (blocking) return;
  blocking = true;
  const dialog = document.createElement("dialog");
  dialog.setAttribute("aria-labelledby", "frontend-update-title");
  dialog.setAttribute("aria-describedby", "frontend-update-description");
  dialog.style.cssText =
    "margin:auto;padding:28px;max-width:440px;width:calc(100% - 40px);box-sizing:border-box;border:1px solid #475569;border-radius:16px;background:#111827;color:#f3f4f6;font:16px/1.5 system-ui;box-shadow:0 20px 80px #0008";
  const style = document.createElement("style");
  style.textContent = "dialog[data-frontend-update]::backdrop{background:rgb(0 0 0 / 75%)}";
  dialog.dataset.frontendUpdate = "";
  const heading = document.createElement("h1");
  heading.id = "frontend-update-title";
  heading.textContent = "Cove has updated — refresh to continue";
  heading.style.cssText = "font-size:22px;font-weight:600;margin:0 0 16px";
  const description = document.createElement("p");
  description.id = "frontend-update-description";
  description.textContent =
    "Refreshing interrupts playback and discards unsaved edits. Your sign-in and preferences will be kept.";
  description.style.cssText = "margin:0 0 24px";
  const button = document.createElement("button");
  button.textContent = "Refresh";
  button.type = "button";
  button.style.cssText =
    "background:#2563eb;color:white;border:0;border-radius:8px;padding:10px 20px;font:inherit;font-weight:600;cursor:pointer";
  button.addEventListener("click", reload);
  dialog.addEventListener("cancel", (event) => event.preventDefault());
  dialog.append(heading, description, button);
  document.head.append(style);
  document.body.append(dialog);
  dialog.showModal();
  button.focus();
}
