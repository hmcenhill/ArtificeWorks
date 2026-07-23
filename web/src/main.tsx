import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";

import { AppLayout } from "./AppLayout";
import { BoardView } from "./views/BoardView";
import { OrderDetailView } from "./views/OrderDetailView";
import "./index.css";

const root = document.getElementById("root");
if (!root) {
  throw new Error("Root element #root not found.");
}

createRoot(root).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<AppLayout />}>
          <Route index element={<BoardView />} />
          <Route path="orders/:id" element={<OrderDetailView />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </StrictMode>,
);
