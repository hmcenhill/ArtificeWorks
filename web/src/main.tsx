import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";

import { AppLayout } from "./AppLayout";
import { RealtimeProvider } from "./realtime/RealtimeProvider";
import { ArchitectureView } from "./views/ArchitectureView";
import { BoardView } from "./views/BoardView";
import { ControlsView } from "./views/ControlsView";
import { CreateOrderView } from "./views/CreateOrderView";
import { DeadLetterDetailView } from "./views/DeadLetterDetailView";
import { DeadLettersView } from "./views/DeadLettersView";
import { OrderDetailView } from "./views/OrderDetailView";
import "./index.css";

const root = document.getElementById("root");
if (!root) {
  throw new Error("Root element #root not found.");
}

createRoot(root).render(
  <StrictMode>
    <BrowserRouter>
      <RealtimeProvider>
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<BoardView />} />
            <Route path="architecture" element={<ArchitectureView />} />
            <Route path="create" element={<CreateOrderView />} />
            <Route path="controls" element={<ControlsView />} />
            <Route path="dead-letters" element={<DeadLettersView />} />
            <Route path="dead-letters/:id" element={<DeadLetterDetailView />} />
            <Route path="orders/:id" element={<OrderDetailView />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </RealtimeProvider>
    </BrowserRouter>
  </StrictMode>,
);
