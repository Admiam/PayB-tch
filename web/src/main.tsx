import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";

import "./styles/global.css";
import "./styles/components.css";
import "./styles/sheet.css";
import "./styles/dashboard.css";
import "./styles/onboarding.css";
import "./styles/expense.css";
import "./styles/detail.css";
import "./styles/settings.css";
import "./styles/shared-debt.css";
import "./styles/activity.css";
import "./styles/auth.css";
import "./styles/invite.css";

const container = document.getElementById("root");
if (!container) throw new Error("Root element missing from index.html");

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
