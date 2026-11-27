import { bootstrapApplication } from "@angular/platform-browser";
import { provideRouter, withComponentInputBinding, withPreloading } from "@angular/router";
import { App } from "./app/app";
import { PreloadPages, routes } from "./app/app.routes";

bootstrapApplication(App, {
  providers: [provideRouter(routes, withComponentInputBinding(), withPreloading(PreloadPages))],
}).catch((err: unknown) => console.error(err));
