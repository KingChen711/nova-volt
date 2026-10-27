import type { NextConfig } from "next";

// Web chỉ là giao diện: mọi dữ liệu đi qua OData và command API của MOM (AGENTS.md K10), không có route API riêng.
const config: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  // Huy hiệu dev của Next đè lên góc dưới sidebar (avatar, nút đăng xuất).
  devIndicators: false,
};

export default config;
