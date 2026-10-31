import type { Metadata, Viewport } from "next";
import { Open_Sans } from "next/font/google";
import { AuthProvider } from "@/lib/auth";
import "./globals.css";

const sans = Open_Sans({ subsets: ["latin", "vietnamese"], weight: ["400", "600", "700"], variable: "--font-open-sans" });

export const metadata: Metadata = {
  title: "NovaVolt MOM",
  description: "Giao diện vận hành MOM của nhà máy NovaVolt (dự án học tập)",
};

export const viewport: Viewport = { width: "device-width", initialScale: 1 };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="vi" className={sans.variable}>
      <body className="min-h-dvh font-sans antialiased">
        <AuthProvider>{children}</AuthProvider>
      </body>
    </html>
  );
}
