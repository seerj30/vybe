import { InjectionToken } from "@angular/core";
import { ApiClient } from "./api-client";

/** The app-wide API client. Tests provide their own instance. */
export const API = new InjectionToken<ApiClient>("ApiClient", { providedIn: "root", factory: () => new ApiClient() });
