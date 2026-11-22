import { button, el, text } from "./dom";

export class Confirmation {
  readonly root = el("dialog", "court-confirm");
  private readonly title = el("h2");
  private readonly description = el("p", "muted");
  private readonly cancel = button("Cancel", "button secondary", () => this.close());
  private readonly proceed = button("", "button primary", () => {
    const action = this.action;
    this.close();
    action?.();
  });
  private action?: () => void;
  private opener?: HTMLElement;

  constructor() {
    this.title.id = "court-confirm-title";
    this.description.id = "court-confirm-description";
    this.root.setAttribute("aria-labelledby", this.title.id);
    this.root.setAttribute("aria-describedby", this.description.id);
    this.root.addEventListener("close", () => this.restoreFocus());
    this.root.append(this.title, this.description, el("div", "setup-actions", this.cancel, this.proceed));
  }

  show(label: string, description: string, opener: HTMLElement, action: () => void): void {
    this.opener = opener;
    this.action = action;
    text(this.title, `${label}?`);
    text(this.description, description);
    text(this.proceed, label);
    this.root.showModal();
    this.cancel.focus();
  }

  close(): void {
    if (this.root.open) this.root.close();
    this.restoreFocus();
  }

  private restoreFocus(): void {
    if (this.root.open) return;
    this.opener?.focus();
    this.opener = undefined;
    this.action = undefined;
  }
}
