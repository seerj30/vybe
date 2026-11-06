import { bootstrapApplication } from "@angular/platform-browser";
import { provideRouter, withComponentInputBinding } from "@angular/router";
import { App } from "./app/app";
import { routes } from "./app/app.routes";

bootstrapApplication(App, { providers: [provideRouter(routes, withComponentInputBinding())] }).catch((err: unknown) =>
  console.error(err),
);
