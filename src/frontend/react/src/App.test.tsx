import { render, screen } from "@testing-library/react";
import App from "./App";

describe("React feature parity", () => {
  beforeEach(() => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes("/api/products")) {
          return new Response(
            JSON.stringify([
              {
                productId: "11111111-1111-1111-1111-111111111111",
                name: "Starter Keyboard",
                description: "Entry-level keyboard",
                price: 39.99,
              },
            ]),
            { status: 200, headers: { "Content-Type": "application/json" } },
          );
        }

        return new Response("{}", {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("renders shared feature sections and checkout action", async () => {
    render(<App />);

    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Online Store");
    expect(screen.getByRole("heading", { level: 2, name: "Login" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 2, name: "Register" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 2, name: "Catalog" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 2, name: "Cart" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Checkout" })).toBeInTheDocument();
  });
});
