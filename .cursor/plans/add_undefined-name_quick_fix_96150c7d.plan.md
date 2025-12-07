---
name: Add undefined-name quick fix
overview: Add a language-server quick fix that offers creating a new parameter or variable when a BCP057 undefined-name diagnostic is selected, and validate it with integration tests.
todos:
  - id: review-plumbing
    content: Review code action wiring and BCP057 production
    status: completed
  - id: implement-provider
    content: Add undefined-name provider and hook into handler
    status: completed
    dependencies:
      - review-plumbing
  - id: add-tests
    content: Add integration tests for new quick fixes
    status: completed
    dependencies:
      - implement-provider
---

# Add undefined-name quick fix

- Study the current code action plumbing and BCP057 emission to confirm the right hook points (`src/Bicep.LangServer/Handlers/BicepCodeActionHandler.cs`, `src/Bicep.Core/Diagnostics/DiagnosticBuilder.cs`, and relevant binder logic in `src/Bicep.Core/Semantics`).
- Implement a new `ICodeFixProvider` that detects BCP057/unknown identifier scenarios in the requested range and produces "Create parameter" / "Create variable" quick fixes, wiring it into the providers list in `BicepCodeActionHandler` (likely under `src/Bicep.LangServer/Refactor/`).
- Add integration coverage (e.g., in `src/Bicep.LangServer.IntegrationTests/CodeActionTests.cs` or a new dedicated test) to ensure the code actions appear for an undefined name and apply clean edits, and that they stay hidden when the symbol is already declared.