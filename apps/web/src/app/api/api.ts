import { InjectionToken, inject } from "@angular/core";
import { AuthSession } from "../auth/auth-session";
import { ApiClient } from "./api-client";

/** The app-wide API client. Tests provide their own instance. */
export const API = new InjectionToken<ApiClient>("ApiClient", {
  providedIn: "root",
  factory: () => {
    const client = new ApiClient();
    const auth = inject(AuthSession);
    client.accessToken = () => auth.accessToken();
    return client;
  },
});
