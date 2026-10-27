import type { Metadata, Viewport } from "next";
import { Fira_Code, Fira_Sans } from "next/font/google";
import { AuthProvider } from "@/lib/auth";
import "./globals.css";

const sans = Fira_Sans({ subsets: ["latin", "vietnamese"], weight: ["400", "500", "600", "700"], variable: "--font-fira-sans" });
const mono = Fira_Code({ subsets: ["latin"], weight: ["400", "500", "600"], variable: "--font-fira-code" });

export const metadata: Metadata = {
  title: "NovaVolt MOM",
  description: "Giao diện vận hành MOM của nhà máy NovaVolt (dự án học tập)",
};

export const viewport: Viewport = { width: "device-width", initialScale: 1 };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="vi" className={`${sans.variable} ${mono.variable}`}>
      <body className="min-h-dvh font-sans antialiased">
        <AuthProvider>{children}</AuthProvider>
      </body>
    </html>
  );
}
