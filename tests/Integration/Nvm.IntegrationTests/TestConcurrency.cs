// Mỗi collection có thể khởi động SQL Server/PostgreSQL/broker riêng. Số CPU của host không
// phản ánh RAM Docker (rig local 8GB); chạy đồng loạt đã gây SQL701/exit118 lúc startup.
// Giữ concurrency có giới hạn; các test race bên trong vẫn giữ toàn bộ request song song.
[assembly: Xunit.v3.Parallelization(MaxThreads = 2)]
