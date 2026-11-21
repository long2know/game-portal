export function el<K extends keyof HTMLElementTagNameMap>(
  tag: K, className = "", ...children: (Node | string)[]
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) node.className = className;
  node.append(...children);
  return node;
}

export function button(label: string, className = "button", activate?: () => void): HTMLButtonElement {
  const node = el("button", className, label);
  node.type = "button";
  if (activate) node.addEventListener("click", activate);
  return node;
}

export function link(label: string, href: string, className = ""): HTMLAnchorElement {
  const node = el("a", className, label);
  node.href = href;
  return node;
}

export function text(node: Node, value: string): void {
  if (node.textContent !== value) node.textContent = value;
}

export function svg<K extends keyof SVGElementTagNameMap>(
  tag: K, attrs: Record<string, string | number>,
): SVGElementTagNameMap[K] {
  const node = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
  return node;
}
