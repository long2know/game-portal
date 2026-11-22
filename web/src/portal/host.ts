import type { Guest } from "../protocol";
import type { portalApi } from "./api";
import type { onlineLobby } from "./social-entry";

type PortalApi = ReturnType<typeof portalApi>;
export type ProfileChange = (current: Guest) => Parameters<PortalApi["profile"]>[0];

export interface PortalHost {
  main: HTMLElement;
  mount: string;
  api: PortalApi;
  openGuest: () => Promise<Guest>;
  changeProfile: (change: ProfileChange) => Promise<Guest>;
  forgetGuest: () => void;
  social: ReturnType<typeof onlineLobby>;
}

export const readInvite = () => new URLSearchParams(location.hash.slice(1)).get("invite");
